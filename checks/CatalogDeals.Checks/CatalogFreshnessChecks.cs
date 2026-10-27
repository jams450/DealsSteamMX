using System.Text.Json;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Behaviour checks for the catalog price-freshness monitor and the pending-price batch selection
/// (spec S4/S5/S6).
///
/// Everything runs offline. The query builders are <see cref="IQueryable{T}"/> compositions, so each one is
/// executed twice here:
/// <list type="number">
/// <item>over in-memory entity lists, which asserts the actual classification, ordering, counting and
/// pending-selection behaviour — the freshness buckets are a price-coverage statement, and these checks pin
/// the bucket boundaries (72h), the region/currency/comparability gates, the future-stamp refusal and the
/// "each canonical game once, Steam-less included" rule;</item>
/// <item>with <c>ToQueryString()</c> against a real <see cref="AppDbContext"/> on the Npgsql provider with a
/// dummy connection string, which proves the productions provider translates the shape (no connection is
/// opened, so no database is touched).</item>
/// </list>
///
/// The predicates that decide "is this a usable comparable MXN price" are shared with the discover catalog
/// instead of duplicated, so this file also pins that the price-only half of the rule admits a stale row
/// (stale is not missing) while still refusing an unconverted, non-positive, foreign-region or Epic-free row.
/// </summary>
internal static class CatalogFreshnessChecks
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly List<string> Failures = [];

    public static void Run()
    {
        WindowAndStatusTokens();
        StatusClassification();
        PriceComparabilityIsShared();
        GameStateBuckets();
        CountsAndFilter();
        PageOrderAndBounds();
        ProviderDates();
        PendingSelection();
        RefreshSelectionSkipsRecentlyAttempted();
        SteamDirectPriceIsEligible();
        CheapestStoredPriceDecidesCoverage();
        RefreshDoesNotCountAFreshSteamPriceAsTheMonitorsPrice();
        HttpContractShape();
        WinnerQueriesAreValidSql();
        EfTranslationIsObserved();

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: {Failures.Count} catalog freshness checks failed");
            foreach (var failure in Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            Environment.Exit(1);
        }

        Console.WriteLine("PASS: catalog freshness checks");
    }

    private static void Check(bool value, string name)
    {
        if (!value)
        {
            Failures.Add(name);
        }
    }

    private static void CheckEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    // --- Fixtures ---------------------------------------------------------

    private static Game GameRow(long gameId, string title = "Juego") =>
        new() { GameId = gameId, Title = title, NormalizedTitle = title };

    private static GameOffer Offer(
        long gameId,
        DateTime observedAt,
        string source = "itad",
        string region = "mx",
        string currency = "MXN",
        int? price = 9_999,
        int? originalPrice = 9_999,
        string pricingType = "regional",
        string? offerKey = null,
        string? shopName = null) =>
        new()
        {
            GameOfferId = gameId * 100 + (long)(observedAt.Ticks % 100),
            GameId = gameId,
            SteamGameId = 42,
            Region = region,
            Source = source,
            OfferKey = offerKey ?? source,
            ShopName = shopName ?? source,
            Classification = "official",
            OriginalCurrency = currency,
            OriginalRegularPriceMinor = originalPrice,
            OriginalCurrentPriceMinor = originalPrice,
            MxnRegularPriceMinor = price,
            MxnCurrentPriceMinor = price,
            PricingType = pricingType,
            ObservedAt = observedAt,
        };

    private static SteamGame Steam(
        long gameId,
        DateTime observedAt,
        int appId = 1_057_800,
        int steamGameId = 42,
        int current = 9_999,
        string currency = "MXN",
        string region = "mx") =>
        new()
        {
            SteamGameId = steamGameId,
            AppId = appId,
            Name = "Juego",
            Region = region,
            Currency = currency,
            CurrentPriceMinor = current,
            InitialPriceMinor = 20_000,
            ObservedAt = observedAt,
            GameId = gameId,
        };

    // --- Checks -----------------------------------------------------------

    private static void WindowAndStatusTokens()
    {
        // The UI contract states this number, so it is a constant and not the configurable notifier window.
        CheckEqual(72, CatalogFreshness.Hours, "the freshness window is 72 hours");
        CheckEqual(TimeSpan.FromHours(72), CatalogFreshness.Window, "the window is three days");
        CheckEqual(
            Now.AddHours(-72),
            CatalogFreshness.Cutoff(Now),
            "the cutoff is asOfUtc minus the window");

        foreach (var token in new[] { "all", "recent", "stale", "missing" })
        {
            Check(CatalogFreshnessStatus.TryParse(token, out var parsed), $"status token '{token}' parses");
            CheckEqual(token, parsed, $"status token '{token}' round-trips");
        }

        Check(CatalogFreshnessStatus.TryParse("  RECENT ", out var mixed), "status token is trimmed and case-folded");
        CheckEqual("recent", mixed, "the normalized token is the stable lowercase one");
        Check(!CatalogFreshnessStatus.TryParse("nuevo", out _), "an unknown status token is refused");
        Check(!CatalogFreshnessStatus.TryParse(null, out _), "a missing status token is refused by TryParse");

        // The default filter is `all`: the panel opens on the whole catalog and narrows on request.
        CheckEqual("all", CatalogFreshnessStatus.Parse(null), "an absent status means all");

        var threw = false;
        try
        {
            CatalogFreshnessStatus.Parse("nuevo");
        }
        catch (ArgumentException exception)
        {
            threw = true;
            Check(exception.Message.Contains("nuevo", StringComparison.Ordinal), "the rejection names the token");
            Check(exception.Message.Contains("recent", StringComparison.Ordinal), "the rejection lists the valid set");
        }

        Check(threw, "an unknown status token throws ArgumentException, never a silent default");

        // The paging bounds are the ones the discover catalog already uses: pageSize 20 by default, hard
        // maximum 50, and page 1 for anything below it.
        CheckEqual(20, CatalogDealPager.DefaultPageSize, "the default page size is 20");
        CheckEqual(50, CatalogDealPager.MaxPageSize, "the hard page-size bound is 50");
        CheckEqual(1, CatalogDealPager.NormalizePage(0), "page 0 is clamped to 1");
        CheckEqual(20, CatalogDealPager.NormalizePageSize(0), "page size 0 falls back to the default");
        CheckEqual(50, CatalogDealPager.NormalizePageSize(10_000), "an oversized page is clamped");
    }

    private static void StatusClassification()
    {
        CheckEqual(
            CatalogFreshnessStatus.Missing,
            CatalogFreshnessStatus.Classify(null, Now),
            "no usable price means missing");

        CheckEqual(
            CatalogFreshnessStatus.Recent,
            CatalogFreshnessStatus.Classify(Now.AddHours(-1), Now),
            "a price one hour old is recent");

        CheckEqual(
            CatalogFreshnessStatus.Recent,
            CatalogFreshnessStatus.Classify(Now.AddHours(-72), Now),
            "a price exactly 72h old is still inside the window (boundary is inclusive)");

        CheckEqual(
            CatalogFreshnessStatus.Stale,
            CatalogFreshnessStatus.Classify(Now.AddHours(-72).AddSeconds(-1), Now),
            "a price one second past the window is stale");

        CheckEqual(
            CatalogFreshnessStatus.Stale,
            CatalogFreshnessStatus.Classify(Now.AddDays(-30), Now),
            "a month-old price is stale, not missing: it was observed");

        CheckEqual(
            CatalogFreshnessStatus.Recent,
            CatalogFreshnessStatus.Classify(Now, Now),
            "a price observed at the reference instant is recent");

        // A future stamp is corrupt evidence: it must not read as fresh, and it must not read as stale either.
        CheckEqual(
            CatalogFreshnessStatus.Missing,
            CatalogFreshnessStatus.Classify(Now.AddHours(1), Now),
            "a future stamp is not valid freshness evidence");
    }

    private static void PriceComparabilityIsShared()
    {
        var usable = CatalogFreshnessQueries.UsableOffer(Now).Compile();

        Check(usable(Offer(1, Now.AddHours(-1))), "a comparable MXN offer is usable");
        Check(usable(Offer(1, Now.AddDays(-40))), "an old comparable offer is still usable (stale is not missing)");
        Check(!usable(Offer(1, Now.AddHours(-1), region: "us")), "another region's offer is not usable");
        Check(
            usable(Offer(1, Now.AddHours(-1), currency: "USD", pricingType: "fx_estimate")),
            "a converted amount (foreign quote, MXN price) is usable coverage");
        Check(
            !usable(Offer(1, Now.AddHours(-1), currency: "USD", pricingType: "unconverted", price: null)),
            "an unconverted row has no MXN amount and is not usable");
        Check(!usable(Offer(1, Now.AddHours(-1), price: 0)), "a zero price is not usable");
        Check(!usable(Offer(1, Now.AddHours(-1), price: -5)), "a negative price is not usable");
        Check(!usable(Offer(1, Now.AddHours(-1), price: 500_000_001)), "a corrupt price is not usable");
        Check(!usable(Offer(1, Now.AddHours(-1), pricingType: "unconverted")), "an unconverted amount is not usable");
        Check(
            !usable(Offer(1, Now.AddHours(-1), source: "epic", originalPrice: null)),
            "Epic's price-less/free row is not usable");
        Check(!usable(Offer(1, Now.AddHours(2))), "a future observation is not usable");

        // The Steam direct snapshot is gated the same way, and a Steam-less game simply contributes nothing.
        var steamUsable = CatalogFreshnessQueries.UsableSteamDirect(Now).Compile();
        Check(steamUsable(Steam(1, Now.AddDays(-3))), "an old Steam direct price is still usable");
        Check(!steamUsable(Steam(1, Now.AddHours(-1), currency: "USD")), "a non-MXN Steam price is not usable");
        Check(!steamUsable(Steam(1, Now.AddHours(-1), region: "us")), "a foreign Steam region is not usable");
        Check(!steamUsable(Steam(1, Now.AddHours(-1), current: 0)), "a zero Steam price is not usable");
        Check(!steamUsable(Steam(1, Now.AddHours(5))), "a future Steam observation is not usable");

        // And it is the same rule the discover catalog uses: `CanonicalOffer` is the price rule plus the same
        // MX region gate and the same past-only bound, so a row usable here is comparable there and nowhere
        // else. The catalog's freshness window is the only intended difference, and it is applied below.
        var comparable = CatalogDealQuery.CanonicalOffer(Now, TimeSpan.FromHours(72)).Compile();
        foreach (var price in new int?[] { 9_999, 0, -1, 600_000_000 })
        {
            foreach (var region in new[] { "mx", "us" })
            {
                var row = Offer(1, Now.AddHours(-1), region: region, price: price);
                CheckEqual(
                    usable(row),
                    comparable(row),
                    $"the freshness and catalog price rules agree for price={price?.ToString() ?? "null"} region={region}");
                // An old row is coverage for the monitor and not a fresh deal for the catalog: that is the
                // whole difference between the two windows, and it is deliberate. `stale` is a real
                // observation, so it must never be reported as `missing`.
                var old = Offer(1, Now.AddDays(-30), region: region, price: price);
                CheckEqual(usable(old), price is > 0 and <= 500_000_000 && region == "mx",
                    $"a stale row is still price coverage (region={region} price={price?.ToString() ?? "null"})");
                Check(!comparable(old),
                    $"a stale row is never a fresh catalog row (region={region} price={price?.ToString() ?? "null"})");
            }
        }
    }

    private static void GameStateBuckets()
    {
        var games = new List<Game>
        {
            GameRow(1, "Reciente"),
            GameRow(2, "Antiguo"),
            GameRow(3, "Sin precio"),
            GameRow(4, "Sin Steam"),
            GameRow(5, "Solo futuro"),
        }.AsQueryable();

        var offers = new List<GameOffer>
        {
            Offer(1, Now.AddHours(-2)),
            Offer(2, Now.AddDays(-9)),
            Offer(2, Now.AddDays(-20)),
            // Steam-less game: the offer hangs off game_id alone and is still a first-class member.
            Offer(4, Now.AddDays(-4), source: "microsoft"),
            // Only evidence is corrupt, so it counts as no evidence at all.
            Offer(5, Now.AddHours(9)),
        }.AsQueryable();

        var steam = new List<SteamGame>
        {
            Steam(1, Now.AddHours(-1)),
            Steam(2, Now.AddDays(-10), steamGameId: 43, appId: 200),
            Steam(3, Now.AddDays(-1), steamGameId: 44, appId: 300, current: 0),
        }.AsQueryable();

        var prices = CatalogFreshnessQueries.PriceUniverse(offers, steam, Now);
        var states = CatalogFreshnessQueries.GameStates(games, prices, Now).ToList();

        CheckEqual(5, states.Count, "each canonical game appears exactly once");
        CheckEqual(states.Select(state => state.GameId).Distinct().Count(), states.Count, "no game is duplicated");

        string StatusOf(long gameId) =>
            states.Single(state => state.GameId == gameId).Status;

        CheckEqual("recent", StatusOf(1), "a game with a price inside the window is recent");
        CheckEqual("stale", StatusOf(2), "a game whose usable prices are all old is stale");
        // A Steam price we could not read is not coverage: the game is missing, not stale.
        CheckEqual("missing", StatusOf(3), "a game whose only Steam price is unusable is missing");
        CheckEqual("missing", StatusOf(5), "a game with only future evidence is missing");

        // The Steam-less game is stale, not missing: it has a real price, only an old one.
        CheckEqual("stale", StatusOf(4), "a Steam-less game with an old provider price is stale");

        // The chosen (cheapest) usable price defines the game's status, amount and timestamp; here all three
        // rows cost the same, so the newest observation among them wins.
        var stale = states.Single(state => state.GameId == 2);
        CheckEqual<DateTime?>(Now.AddDays(-9), stale.ChosenObservedAt,
            "between equal amounts the newest observation wins");

        var missing = states.Single(state => state.GameId == 3);
        CheckEqual<DateTime?>(null, missing.ChosenObservedAt, "missing means no usable timestamp at all");

        // A usable row of another region cannot move a game out of `missing`.
        var foreignOnly = CatalogFreshnessQueries.GameStates(
            new List<Game> { GameRow(9) }.AsQueryable(),
            CatalogFreshnessQueries.PriceUniverse(
                new List<GameOffer> { Offer(9, Now.AddHours(-1), region: "us") }.AsQueryable(),
                new List<SteamGame>().AsQueryable(),
                Now),
            Now).ToList();
        CheckEqual("missing", foreignOnly.Single().Status, "a foreign-region price is not coverage");
    }

    private static void CountsAndFilter()
    {
        var games = new List<Game> { GameRow(1), GameRow(2), GameRow(3), GameRow(4) }.AsQueryable();
        var offers = new List<GameOffer>
        {
            Offer(1, Now.AddHours(-1)),
            Offer(2, Now.AddDays(-8)),
            Offer(3, Now.AddDays(-9)),
        }.AsQueryable();
        var steam = new List<SteamGame>().AsQueryable();

        var states = CatalogFreshnessQueries.GameStates(
            games,
            CatalogFreshnessQueries.PriceUniverse(offers, steam, Now),
            Now);

        var counts = CatalogFreshnessCounts.Of(CatalogFreshnessQueries.Counts(states).ToList());
        CheckEqual(4, counts.Total, "the total counts every canonical game");
        CheckEqual(1, counts.Recent, "one recent game");
        CheckEqual(2, counts.Stale, "two stale games");
        CheckEqual(1, counts.Missing, "one missing game");

        // Counts are computed over the whole catalog and are never narrowed by the selected filter.
        foreach (var status in CatalogFreshnessStatus.Filterable)
        {
            var filtered = CatalogFreshnessQueries.Filter(states, status).ToList();
            var expected = status switch
            {
                "all" => 4,
                "recent" => 1,
                "stale" => 2,
                _ => 1,
            };
            CheckEqual(expected, filtered.Count, $"the '{status}' filter selects its own bucket");
            Check(
                filtered.All(state => state.Status == status || status == "all"),
                $"the '{status}' filter never mixes buckets");
        }

        // An unknown token never reaches the query: Parse already refused it.

        // A status with no members still reports zeros instead of omitting the field.
        var empty = CatalogFreshnessCounts.Of([]);
        CheckEqual(0, empty.Total, "an empty catalog counts zero");
        CheckEqual(0, empty.Recent, "an empty catalog counts no recent game");
        CheckEqual(0, empty.Stale, "an empty catalog counts no stale game");
        CheckEqual(0, empty.Missing, "an empty catalog counts no missing game");
    }

    private static void PageOrderAndBounds()
    {
        var games = new List<Game>
        {
            GameRow(10), GameRow(20), GameRow(30), GameRow(40),
        }.AsQueryable();
        var offers = new List<GameOffer>
        {
            Offer(10, Now.AddDays(-20)),
            Offer(20, Now.AddDays(-5)),
        }.AsQueryable();

        var states = CatalogFreshnessQueries.GameStates(
            games,
            CatalogFreshnessQueries.PriceUniverse(offers, new List<SteamGame>().AsQueryable(), Now),
            Now);

        // Missing first (no evidence is the most urgent), then the oldest observation, then game id.
        var ordered = CatalogFreshnessQueries.Page(states, 0, 20).Select(state => state.GameId).ToList();
        CheckEqual("30,40,10,20", string.Join(",", ordered), "missing games come first, then oldest price first");

        // The order is total: two pages never overlap and never drop a game.
        var first = CatalogFreshnessQueries.Page(states, 0, 2).Select(state => state.GameId).ToList();
        var second = CatalogFreshnessQueries.Page(states, 2, 2).Select(state => state.GameId).ToList();
        CheckEqual(2, first.Count, "the first page is bounded by the page size");
        CheckEqual(0, first.Intersect(second).Count(), "pages do not overlap");
        CheckEqual(4, first.Concat(second).Distinct().Count(), "two pages cover the whole catalog");

        // A page past the end is empty rather than an error or a restart.
        CheckEqual(0, CatalogFreshnessQueries.Page(states, 40, 20).ToList().Count, "a page past the end is empty");
    }

    private static void ProviderDates()
    {
        var steam = new CatalogFreshnessSteamRow
        {
            GameId = 1,
            SteamGameId = 42,
            AppId = 1_057_800,
            ObservedAt = Now.AddHours(-2),
            OffersRefreshedAt = Now.AddDays(-1),
            GgDealsRefreshedAt = Now.AddDays(-4),
            EpicRefreshedAt = Now.AddHours(6), // corrupt/future: must not be shown as freshness
            MicrosoftRefreshedAt = null,
        };

        var rows = new List<CatalogFreshnessPriceRow>
        {
            new() { GameId = 1, Provider = "itad", ObservedAt = Now.AddDays(-1) },
            new() { GameId = 1, Provider = "itad", ObservedAt = Now.AddDays(-2) },
            new() { GameId = 1, Provider = "ggdeals", ObservedAt = Now.AddDays(-5) },
            new() { GameId = 1, Provider = "steam", ObservedAt = Now.AddHours(-2) },
            new() { GameId = 1, Provider = "epic", ObservedAt = Now.AddHours(6) },
        };

        var dates = CatalogFreshnessProviders.Build(rows, steam, Now);
        var byProvider = dates.ToDictionary(date => date.Provider, StringComparer.Ordinal);

        CheckEqual("ggdeals,itad,steam", string.Join(",", dates.Select(d => d.Provider)),
            "providers are ordered deterministically");

        CheckEqual<DateTime?>(Now.AddDays(-1), byProvider["itad"].PriceObservedAtUtc,
            "a provider's price timestamp is its latest observation");
        CheckEqual<DateTime?>(Now.AddDays(-1), byProvider["itad"].LastCheckedAtUtc,
            "the ITAD consultation stamp is reported separately from the price");
        CheckEqual<DateTime?>(Now.AddDays(-5), byProvider["ggdeals"].PriceObservedAtUtc,
            "partial staleness is visible per provider");
        CheckEqual<DateTime?>(Now.AddDays(-4), byProvider["ggdeals"].LastCheckedAtUtc,
            "gg.deals keeps its own consultation stamp");

        // A future stamp is corrupt: it is reported as neither a price date nor a consultation date, so a
        // provider whose only evidence is that stamp has nothing to report.
        Check(!byProvider.ContainsKey("epic"), "a provider whose only evidence is a future stamp is not reported");

        // A provider that was consulted and had nothing comparable: the stamp is its only evidence and it is
        // reported with no price date. That is the honest shape of "unsupported or no match".
        var consultedOnly = CatalogFreshnessProviders.Build(
            [],
            steam with { MicrosoftRefreshedAt = Now.AddDays(-3) },
            Now);
        var microsoft = consultedOnly.Single(date => date.Provider == "microsoft");
        CheckEqual<DateTime?>(null, microsoft.PriceObservedAtUtc,
            "a consulted provider with no usable price reports no price date");
        CheckEqual<DateTime?>(Now.AddDays(-3), microsoft.LastCheckedAtUtc,
            "its consultation stamp is reported");

        // An unknown stamp stays unknown: it never becomes "never attempted" nor a failure.
        Check(
            !CatalogFreshnessProviders.Build([], steam with { EpicRefreshedAt = null }, Now)
                .Any(date => date.Provider == "epic"),
            "a provider with no stamp and no price reports nothing instead of claiming an attempt");

        // Steam's consultation stamp is the snapshot's own observed_at.
        CheckEqual<DateTime?>(Now.AddHours(-2), byProvider["steam"].LastCheckedAtUtc,
            "the Steam snapshot date is Steam's consultation stamp");

        // A Steam-less game has no stamps to report, only whatever its offers prove.
        var steamLess = CatalogFreshnessProviders.Build(
            [new CatalogFreshnessPriceRow { GameId = 2, Provider = "microsoft", ObservedAt = Now.AddDays(-3) }],
            null,
            Now);
        CheckEqual(1, steamLess.Count, "a Steam-less game lists only the providers it has evidence for");
        CheckEqual("microsoft", steamLess[0].Provider, "the Steam-less provider is named");
        CheckEqual<DateTime?>(Now.AddDays(-3), steamLess[0].PriceObservedAtUtc, "its price date is reported");
        CheckEqual<DateTime?>(null, steamLess[0].LastCheckedAtUtc, "and its unknown stamp stays null");

        CheckEqual(0, CatalogFreshnessProviders.Build([], null, Now).Count, "a game with no evidence lists no provider");
    }

    private static void PendingSelection()
    {
        CheckEqual(20, CatalogPriceRefreshLimits.BatchSize, "the refresh batch is hard bounded to 20 games");

        var games = new List<Game>
        {
            GameRow(1), GameRow(2), GameRow(3), GameRow(4), GameRow(5), GameRow(6),
        }.AsQueryable();
        var offers = new List<GameOffer>
        {
            Offer(1, Now.AddDays(-10)),  // stale
            Offer(2, Now.AddHours(-1)),  // recent: never pending
            Offer(3, Now.AddDays(-30)),  // older stale
            Offer(6, Now.AddDays(-5)),   // stale, newer than 3 and 1
            Offer(5, Now.AddDays(-4), source: "microsoft"), // stale and Steam-less
        }.AsQueryable();
        var steamGames = new List<SteamGame>
        {
            Steam(1, Now.AddDays(-11), steamGameId: 42, appId: 100),
            Steam(2, Now.AddDays(-1), steamGameId: 43, appId: 200),
            Steam(1, Now.AddDays(-9), steamGameId: 44, appId: 101), // second app id of the same game
            Steam(3, Now.AddDays(-31), steamGameId: 45, appId: 300),
            // A snapshot with no usable MXN price: game 4 is `missing`, and still refreshable by app id.
            Steam(4, Now.AddDays(-2), steamGameId: 46, appId: 400, current: 0),
            Steam(6, Now.AddDays(-6), steamGameId: 47, appId: 500),
        }.AsQueryable();

        var states = CatalogFreshnessQueries.GameStates(
            games,
            CatalogFreshnessQueries.PriceUniverse(offers, steamGames, Now),
            Now);
        var pending = CatalogFreshnessQueries.Pending(states).ToList();

        CheckEqual("1,3,4,5,6", string.Join(",", pending.Select(state => state.GameId).OrderBy(id => id)),
            "only stale and missing games are pending");
        Check(!pending.Any(state => state.GameId == 2), "a fresh game is never a candidate");

        var candidates = CatalogFreshnessQueries
            .PendingCandidates(pending.AsQueryable(), steamGames, CatalogPriceRefreshLimits.BatchSize, Now)
            .ToList();
        CheckEqual("4,3,1,6", string.Join(",", candidates.Select(candidate => candidate.GameId)),
            "pending candidates are missing first, then oldest price first");

        // The Steam-less game has no Steam app id to refresh with: it is counted, never selected.
        Check(!candidates.Any(candidate => candidate.GameId == 5), "a Steam-less game is not selected for refresh");
        CheckEqual(1, pending.Count(state => state.GameId == 5), "a Steam-less game stays pending and visible");

        // One canonical game, one refresh: the game with two Steam snapshots is selected once.
        CheckEqual(1, candidates.Count(candidate => candidate.GameId == 1), "a game with two snapshots is selected once");
        CheckEqual(100, candidates.Single(candidate => candidate.GameId == 1).AppId,
            "the lowest Steam snapshot is the deterministic choice");

        // The batch bound lives in the candidate query: 25 refreshable pending games still yield 20.
        var manyGames = Enumerable.Range(100, 25).Select(id => GameRow(id)).ToList();
        var manyOffers = Enumerable.Range(100, 25)
            .Select(id => Offer(id, Now.AddDays(-10)))
            .ToList();
        var manySteam = Enumerable.Range(100, 25)
            .Select(id => Steam(id, Now.AddDays(-11), steamGameId: id, appId: id * 10))
            .ToList();
        var manyStates = CatalogFreshnessQueries.GameStates(
            manyGames.AsQueryable(),
            CatalogFreshnessQueries.PriceUniverse(
                manyOffers.AsQueryable(),
                manySteam.AsQueryable(),
                Now),
            Now);
        var bounded = CatalogFreshnessQueries
            .PendingCandidates(
                CatalogFreshnessQueries.Pending(manyStates),
                manySteam.AsQueryable(),
                CatalogPriceRefreshLimits.BatchSize,
                Now)
            .ToList();
        CheckEqual(25, CatalogFreshnessQueries.Pending(manyStates).ToList().Count, "25 games are pending");
        CheckEqual(20, bounded.Count, "the batch never exceeds the hard bound");

        var oversized = false;
        try
        {
            CatalogFreshnessQueries
                .PendingCandidates(
                    pending.AsQueryable(),
                    steamGames,
                    CatalogPriceRefreshLimits.BatchSize + 1,
                    Now)
                .ToList();
        }
        catch (ArgumentOutOfRangeException)
        {
            oversized = true;
        }

        Check(oversized, "a batch larger than the hard bound is refused, not silently clamped");

        var unsupported = CatalogFreshnessQueries.PendingWithoutSteam(pending.AsQueryable(), steamGames).ToList();
        CheckEqual("5", string.Join(",", unsupported.Select(state => state.GameId)),
            "the unsupported counter is the pending games with no Steam identity");
    }

    /// <summary>
    /// A pending game is only a *candidate* when at least one provider gate is actually due. Without this, a
    /// title no shop can price (delisted, no MX offer, no identity match) would stay `missing` forever, sort
    /// first in every batch and consume a slot on every run while nothing changed. The gates are the same
    /// authoritative consultation stamps the monitor reports, so a fresh stamp means "we asked recently and
    /// there was nothing comparable" — the game stays pending and visible, it is just not re-asked yet.
    /// </summary>
    private static void RefreshSelectionSkipsRecentlyAttempted()
    {
        var games = new List<Game> { GameRow(1), GameRow(2), GameRow(3), GameRow(4) }.AsQueryable();

        // 1: missing, never consulted (no stamps) -> due.
        // 2: missing, every provider consulted one hour ago -> not due (no-match title, cooled down).
        // 3: stale price, every provider consulted 10 days ago -> due.
        // 4: missing, one provider consulted 2 days ago and one 5 days ago -> due (the older gate is due).
        var steamGames = new List<SteamGame>
        {
            Steam(1, Now.AddDays(-1), steamGameId: 41, appId: 100, current: 0),
            Steam(2, Now.AddDays(-1), steamGameId: 42, appId: 200, current: 0),
            Steam(3, Now.AddDays(-20), steamGameId: 43, appId: 300),
            Steam(4, Now.AddDays(-1), steamGameId: 44, appId: 400, current: 0),
        }.AsQueryable();

        // A real "asked and nothing comparable came back" row advanced every gate: each phase answered.
        var consultedRecently = steamGames.ToList();
        consultedRecently[1] = WithStamps(
            steamGames.Single(game => game.SteamGameId == 42),
            Now.AddHours(-1),
            ggDealsAt: Now.AddHours(-1),
            epicAt: Now.AddHours(-1),
            microsoftAt: Now.AddHours(-1));
        consultedRecently[2] = WithStamps(
            steamGames.Single(game => game.SteamGameId == 43),
            Now.AddDays(-10),
            ggDealsAt: Now.AddDays(-10),
            epicAt: Now.AddDays(-10),
            microsoftAt: Now.AddDays(-10));
        consultedRecently[3] = WithStamps(
            steamGames.Single(game => game.SteamGameId == 44),
            Now.AddDays(-2),
            ggDealsAt: Now.AddDays(-5),
            epicAt: Now.AddDays(-2),
            microsoftAt: Now.AddDays(-2));

        var rows = consultedRecently.AsQueryable();

        var states = CatalogFreshnessQueries.GameStates(
            games,
            CatalogFreshnessQueries.PriceUniverse(
                new List<GameOffer>().AsQueryable(),
                rows,
                Now),
            Now);
        var pending = CatalogFreshnessQueries.Pending(states);
        CheckEqual(4, pending.ToList().Count, "all four games are pending coverage-wise");

        var candidates = CatalogFreshnessQueries
            .PendingCandidates(pending, rows, CatalogPriceRefreshLimits.BatchSize, Now)
            .ToList();
        CheckEqual("1,4,3", string.Join(",", candidates.Select(candidate => candidate.GameId)),
            "a recently consulted no-match title is not re-selected; older gates still are");

        var notDue = CatalogFreshnessQueries.PendingNotDue(pending, rows, Now).ToList();
        CheckEqual("2", string.Join(",", notDue.Select(state => state.GameId)),
            "the cooled-down no-match title is counted separately, not hidden");

        // It is still pending, so the monitor keeps showing it: the cooldown is about asking again, not about
        // pretending the game has a price.
        CheckEqual("missing", states.Single(state => state.GameId == 2).Status,
            "the cooled-down game stays in its coverage bucket");

        // A degraded attempt never advanced the stamps, so the game is due again on the next run.
        var degraded = rows.ToList();
        degraded[1] = WithStamps(
            steamGames.Single(game => game.SteamGameId == 42),
            Now.AddDays(-4),
            ggDealsAt: Now.AddDays(-4),
            epicAt: Now.AddDays(-4),
            microsoftAt: Now.AddDays(-4));
        Check(
            CatalogFreshnessQueries
                .PendingCandidates(pending, degraded.AsQueryable(), CatalogPriceRefreshLimits.BatchSize, Now)
                .ToList()
                .Any(candidate => candidate.GameId == 2),
            "an attempt whose provider stamps did not advance is retried, not cooled down");

        // A future stamp is corrupt evidence: it must not block the retry forever.
        var corrupt = rows.ToList();
        corrupt[1] = WithStamps(
            steamGames.Single(game => game.SteamGameId == 42),
            Now.AddHours(3),
            ggDealsAt: Now.AddHours(2),
            epicAt: Now.AddHours(1),
            microsoftAt: Now.AddHours(4));
        Check(
            CatalogFreshnessQueries
                .PendingCandidates(pending, corrupt.AsQueryable(), CatalogPriceRefreshLimits.BatchSize, Now)
                .ToList()
                .Any(candidate => candidate.GameId == 2),
            "a future consultation stamp does not park a game out of the batch forever");
    }

    private static SteamGame WithStamps(
        SteamGame game,
        DateTime? itadAt,
        DateTime? ggDealsAt = null,
        DateTime? epicAt = null,
        DateTime? microsoftAt = null) =>
        new()
        {
            SteamGameId = game.SteamGameId,
            AppId = game.AppId,
            Name = game.Name,
            Region = game.Region,
            Currency = game.Currency,
            CurrentPriceMinor = game.CurrentPriceMinor,
            InitialPriceMinor = game.InitialPriceMinor,
            ObservedAt = game.ObservedAt,
            GameId = game.GameId,
            OffersRefreshedAt = itadAt,
            GgDealsRefreshedAt = ggDealsAt,
            EpicRefreshedAt = epicAt,
            MicrosoftRefreshedAt = microsoftAt,
        };

    /// <summary>
    /// The frozen HTTP contract of <c>GET /api/catalogdeals/freshness</c>. The response records are serialized
    /// with the web defaults the API actually uses, and every key is pinned by name: the BFF and the panel were
    /// built against these names, so a rename here is a breaking change and must be seen offline first.
    /// </summary>
    /// <summary>
    /// The Steam direct snapshot is coverage and therefore has to be refreshable. A game whose only price is a
    /// Steam price older than 72 h must be a candidate even when every external provider answered an hour ago —
    /// otherwise "clean up the pending prices" would quietly mean "clean up the external ones", which is not
    /// what the operator asked for.
    /// </summary>
    private static void SteamDirectPriceIsEligible()
    {
        var games = new List<Game> { GameRow(1), GameRow(2), GameRow(3), GameRow(4) }.AsQueryable();

        // 1: Steam price five days old, every provider stamp one hour old -> Steam is the due phase.
        // 2: Steam price two hours old, every provider stamp one hour old -> nothing is due.
        // 3: Steam price five days old, every provider stamp five days old -> both are due.
        // 4: no usable price, fresh Steam snapshot and fresh provider stamps -> cooled down, still missing.
        var steamGames = new List<SteamGame>
        {
            WithStamps(Steam(1, Now.AddDays(-5), steamGameId: 41, appId: 100), Now.AddHours(-1),
                ggDealsAt: Now.AddHours(-1), epicAt: Now.AddHours(-1), microsoftAt: Now.AddHours(-1)),
            WithStamps(Steam(2, Now.AddHours(-2), steamGameId: 42, appId: 200), Now.AddHours(-1),
                ggDealsAt: Now.AddHours(-1), epicAt: Now.AddHours(-1), microsoftAt: Now.AddHours(-1)),
            WithStamps(Steam(3, Now.AddDays(-6), steamGameId: 43, appId: 300), Now.AddDays(-5),
                ggDealsAt: Now.AddDays(-5), epicAt: Now.AddDays(-5), microsoftAt: Now.AddDays(-5)),
            WithStamps(Steam(4, Now.AddHours(-1), steamGameId: 44, appId: 400, current: 0), Now.AddHours(-1),
                ggDealsAt: Now.AddHours(-1), epicAt: Now.AddHours(-1), microsoftAt: Now.AddHours(-1)),
        }.AsQueryable();

        var states = CatalogFreshnessQueries.GameStates(
            games,
            CatalogFreshnessQueries.PriceUniverse(new List<GameOffer>().AsQueryable(), steamGames, Now),
            Now);

        CheckEqual("stale", states.Single(state => state.GameId == 1).Status,
            "an old Steam price is stale coverage");

        var pending = CatalogFreshnessQueries.Pending(states);
        var candidates = CatalogFreshnessQueries
            .PendingCandidates(pending, steamGames, CatalogPriceRefreshLimits.BatchSize, Now)
            .ToList();
        CheckEqual("3,1", string.Join(",", candidates.Select(candidate => candidate.GameId)),
            "a stale Steam price is selected even when the providers are fresh");

        var notDue = CatalogFreshnessQueries.PendingNotDue(pending, steamGames, Now)
            .Select(state => state.GameId)
            .OrderBy(id => id)
            .ToList();
        // Game 2 is not here because a two-hour-old Steam price makes it `recent`, so it is not pending at all.
        CheckEqual("4", string.Join(",", notDue),
            "only pending games with every phase inside the window are cooled down");
        CheckEqual("recent", states.Single(state => state.GameId == 2).Status,
            "a Steam price inside the window already covers the game");

        CheckEqual(100, candidates.Single(candidate => candidate.GameId == 1).AppId,
            "the selected candidate carries the Steam app id of the chosen snapshot");
        CheckEqual<DateTime?>(Now.AddDays(-5), candidates.Single(candidate => candidate.GameId == 1).ChosenObservedAt,
            "and the price instant that made it pending");

        // A corrupt (future) Steam stamp must not park the game out of the batch.
        var corrupt = new List<SteamGame>
        {
            WithStamps(Steam(1, Now.AddHours(5), steamGameId: 41, appId: 100), Now.AddHours(-1),
                ggDealsAt: Now.AddHours(-1), epicAt: Now.AddHours(-1), microsoftAt: Now.AddHours(-1)),
        }.AsQueryable();
        var corruptStates = CatalogFreshnessQueries.GameStates(
            new List<Game> { GameRow(1) }.AsQueryable(),
            CatalogFreshnessQueries.PriceUniverse(new List<GameOffer>().AsQueryable(), corrupt, Now),
            Now);
        CheckEqual("missing", corruptStates.Single().Status, "a future Steam stamp is not coverage");
        Check(
            CatalogFreshnessQueries
                .PendingCandidates(CatalogFreshnessQueries.Pending(corruptStates), corrupt,
                    CatalogPriceRefreshLimits.BatchSize, Now)
                .ToList()
                .Any(candidate => candidate.GameId == 1),
            "a future Steam stamp keeps the game eligible instead of parking it");
    }

    /// <summary>
    /// User correction S9 («40 juegos &lt; 50 MXN pero en discover solo salen 7 ... 512 con &lt;72h», selected
    /// «El precio más barato guardado (recomendado)»): a game's coverage bucket is decided by the CHEAPEST
    /// stored usable comparable MXN price, not by the newest observation of any shop. A fresh, expensive Steam
    /// price must never hide an older, cheaper external offer — that divergence is exactly why the panel said
    /// `recent` while cheaper prices sat untouched.
    /// </summary>
    private static void CheapestStoredPriceDecidesCoverage()
    {
        var games = new List<Game> { GameRow(1, "Steam caro y fresco"), GameRow(2, "Barato reciente") }.AsQueryable();

        var offers = new List<GameOffer>
        {
            // 30.00 MXN, ten days old: the cheapest price this game has.
            Offer(1, Now.AddDays(-10), price: 3_000, originalPrice: 3_000),
            // 30.00 MXN, one hour old and no Steam snapshot at all: cheap and recent.
            Offer(2, Now.AddHours(-1), price: 3_000, originalPrice: 3_000),
        }.AsQueryable();

        var steam = new List<SteamGame>
        {
            // 399.00 MXN, one hour old. Under the superseded "newest observation" rule this alone made the
            // game `recent`; under the cheapest-price rule it must not.
            WithStamps(
                Steam(1, Now.AddHours(-1), steamGameId: 41, appId: 100, current: 39_900),
                Now.AddDays(-10),
                ggDealsAt: Now.AddDays(-10),
                epicAt: Now.AddHours(-1),
                microsoftAt: Now.AddHours(-1)),
        }.AsQueryable();

        var states = CatalogFreshnessQueries.GameStates(
            games,
            CatalogFreshnessQueries.PriceUniverse(offers, steam, Now),
            Now);

        var mixed = states.Single(state => state.GameId == 1);
        CheckEqual("stale", mixed.Status,
            "the cheapest stored price decides the bucket, never a newer and more expensive one");
        CheckEqual<DateTime?>(Now.AddDays(-10), mixed.ChosenObservedAt,
            "the reported date is the cheap offer's date, not the newest observation of the game");
        CheckEqual<int?>(3_000, mixed.ChosenPriceMinor, "the reported amount is the cheap offer's amount");
        CheckEqual("itad", mixed.ChosenProvider, "the reported source is the cheap offer's source");
        CheckEqual("itad", mixed.ChosenShopName, "the reported shop is the cheap offer's shop");
        CheckEqual("regional", mixed.ChosenPricingType, "the reported pricing type is the cheap offer's type");

        var cheap = states.Single(state => state.GameId == 2);
        CheckEqual("recent", cheap.Status, "a cheap price inside the window is recent");
        CheckEqual<DateTime?>(Now.AddHours(-1), cheap.ChosenObservedAt,
            "a recent cheap offer reports its own date");
        CheckEqual<int?>(3_000, cheap.ChosenPriceMinor, "a recent cheap offer reports its own amount");
        Check(
            cheap.ChosenPriceMinor < DealAlertPolicy.CheapPriceThresholdMinor,
            "the cheap recent game is really under the 50.00 MXN threshold the user filtered by");

        var pending = CatalogFreshnessQueries.Pending(states);
        Check(pending.Any(state => state.GameId == 1),
            "an old cheap winner makes the game pending even while Steam is fresh");
        Check(!pending.Any(state => state.GameId == 2), "a recent cheap winner is not pending");

        var candidates = CatalogFreshnessQueries
            .PendingCandidates(pending, steam, CatalogPriceRefreshLimits.BatchSize, Now)
            .ToList();
        Check(candidates.Any(candidate => candidate.GameId == 1),
            "the manual pass selects a game whose cheap price is stale although Steam was asked an hour ago");

        // Equal cheap amounts are ordered by the newest observation, then by the stable provider name and the
        // provider's own key, so the winner never depends on row order (spec S9: "deterministic tie").
        var tieOffers = new List<GameOffer>
        {
            Offer(3, Now.AddDays(-8), source: "ggdeals", price: 3_000, originalPrice: 3_000),
            Offer(3, Now.AddDays(-4), source: "itad", price: 3_000, originalPrice: 3_000),
            // Same amount and instant as the itad row: the provider name breaks that tie.
            Offer(3, Now.AddDays(-4), source: "ggdeals", price: 3_000, originalPrice: 3_000),
        }.AsQueryable();

        var tieState = CatalogFreshnessQueries.GameStates(
            new List<Game> { GameRow(3) }.AsQueryable(),
            CatalogFreshnessQueries.PriceUniverse(tieOffers, new List<SteamGame>().AsQueryable(), Now),
            Now).Single();

        CheckEqual<DateTime?>(Now.AddDays(-4), tieState.ChosenObservedAt,
            "between equal cheap amounts the newest observation wins");
        CheckEqual("ggdeals", tieState.ChosenProvider,
            "and the provider name breaks a full tie deterministically (ggdeals before itad)");
        CheckEqual<int?>(3_000, tieState.ChosenPriceMinor, "the tied amount is the same cheap amount");

        // Same game, provider, amount and instant, two different offers: the provider key decides, and the shop
        // name proves the reported label really came from that same row.
        var keyTieOffers = new List<GameOffer>
        {
            Offer(5, Now.AddDays(-3), source: "itad", price: 3_000, originalPrice: 3_000,
                offerKey: "zzz", shopName: "GreenManGaming"),
            Offer(5, Now.AddDays(-3), source: "itad", price: 3_000, originalPrice: 3_000,
                offerKey: "aaa", shopName: "Fanatical"),
        }.AsQueryable();

        var keyTieState = CatalogFreshnessQueries.GameStates(
            new List<Game> { GameRow(5) }.AsQueryable(),
            CatalogFreshnessQueries.PriceUniverse(keyTieOffers, new List<SteamGame>().AsQueryable(), Now),
            Now).Single();

        CheckEqual("Fanatical", keyTieState.ChosenShopName,
            "the provider key breaks the last tie, and the shop label comes from that same row");
        CheckEqual("itad", keyTieState.ChosenProvider, "the chosen source is the same row's source");
        CheckEqual("regional", keyTieState.ChosenPricingType, "the chosen pricing type is the same row's type");

        // A cheaper row that is not usable comparable MXN evidence (unconverted, foreign region, or observed
        // in the future) must never become the winner: otherwise the panel would label a game by a price it
        // cannot show.
        var invalidCheap = new List<GameOffer>
        {
            // No MXN figure claimed: an unconverted row, cheaper than the real price.
            Offer(4, Now.AddHours(-1), currency: "USD", price: null, originalPrice: 1_000, pricingType: "unconverted"),
            // Another region's price, cheaper than the real price.
            Offer(4, Now.AddHours(-1), source: "epic", region: "us", price: 500, originalPrice: 500),
            // Corrupt evidence: an observation in the future.
            Offer(4, Now.AddHours(6), price: 400, originalPrice: 400),
            // The one usable comparable MX price.
            Offer(4, Now.AddDays(-20), price: 3_000, originalPrice: 3_000),
        }.AsQueryable();

        var invalidState = CatalogFreshnessQueries.GameStates(
            new List<Game> { GameRow(4) }.AsQueryable(),
            CatalogFreshnessQueries.PriceUniverse(invalidCheap, new List<SteamGame>().AsQueryable(), Now),
            Now).Single();

        CheckEqual<DateTime?>(Now.AddDays(-20), invalidState.ChosenObservedAt,
            "a foreign, unconverted or future row can never win the comparison");
        CheckEqual<int?>(3_000, invalidState.ChosenPriceMinor,
            "the winner is the usable comparable amount, never a blank or converted one");
        CheckEqual("stale", invalidState.Status, "and it cannot make a game look recent either");
    }

    /// <summary>
    /// The post-refresh measurement of the manual pass (S9). "Did this run update the price" is now measured
    /// on the same winner the monitor buckets by: a phase that writes a fresh, more expensive Steam price does
    /// not update a game whose cheap external offer is still the price the panel shows. The stale cheap winner
    /// therefore stays pending, and the honest verdict is `skipped`, never `updated`.
    /// </summary>
    private static void RefreshDoesNotCountAFreshSteamPriceAsTheMonitorsPrice()
    {
        // (a) The provider phase advances Steam's own stamp and price date, but the cheapest price (30.00 MXN,
        // ten days old) does not move: that is not a price update for this monitor.
        using (var db = CreateInMemoryContext())
        {
            var provider = new RepricingSteamGameService(db);
            var service = new CatalogPriceRefreshService(
                new Repository(db),
                provider,
                new FixedClock(Now),
                NullLogger<CatalogPriceRefreshService>.Instance,
                new CatalogWishlistScopeResolver(new Repository(db)));

            var outcome = service.RefreshPendingAsync(1, CancellationToken.None).GetAwaiter().GetResult();

            CheckEqual("100", string.Join(",", provider.Calls), "the stale cheap winner's game is the one refreshed");
            CheckEqual(1, outcome.Selected, "the game with an old cheap price is selected for refresh");
            CheckEqual(0, outcome.Updated,
                "a fresh but more expensive Steam price is not an update of the monitor's price");
            CheckEqual(1, outcome.Skipped, "the run is reported as a skip, not as a successful price write");
            CheckEqual(1, outcome.Remaining, "the game stays pending while the cheap price is old");

            var state = CatalogFreshnessQueries.GameStates(
                new Repository(db).Get<Game>().Where(game => game.GameId == 1),
                CatalogFreshnessQueries.PriceUniverse(
                    new Repository(db).Get<GameOffer>(),
                    new Repository(db).Get<SteamGame>(),
                    Now),
                Now).Single();

            CheckEqual("stale", state.Status,
                "a fresh Steam price does not make the game recent while the cheap offer is old");
            CheckEqual<DateTime?>(Now.AddDays(-10), state.ChosenObservedAt,
                "the monitor keeps showing the cheap offer's date");
            CheckEqual<int?>(3_000, state.ChosenPriceMinor,
                "and the cheap amount, not the fresh expensive one");
            CheckEqual("itad", state.ChosenProvider, "and the source the cheap price came from");
        }

        // (b) Positive control: when the cheap offer really is repriced (and observed now), the same measure
        // must report an update — including a higher amount, because the best price changed.
        using (var db = CreateInMemoryContext())
        {
            var provider = new RepricingSteamGameService(db, newCheapPriceMinor: 4_500);
            var service = new CatalogPriceRefreshService(
                new Repository(db),
                provider,
                new FixedClock(Now),
                NullLogger<CatalogPriceRefreshService>.Instance,
                new CatalogWishlistScopeResolver(new Repository(db)));

            var outcome = service.RefreshPendingAsync(1, CancellationToken.None).GetAwaiter().GetResult();

            CheckEqual(1, outcome.Updated,
                "a cheap offer re-observed at a new price is a real update");
            CheckEqual(0, outcome.Skipped, "and it is not also counted as a skip");

            var state = CatalogFreshnessQueries.GameStates(
                new Repository(db).Get<Game>().Where(game => game.GameId == 1),
                CatalogFreshnessQueries.PriceUniverse(
                    new Repository(db).Get<GameOffer>(),
                    new Repository(db).Get<SteamGame>(),
                    Now),
                Now).Single();

            CheckEqual("recent", state.Status, "the repriced cheap offer makes the game recent");
            CheckEqual<DateTime?>(Now, state.ChosenObservedAt, "the monitor shows the new observation date");
            CheckEqual<int?>(4_500, state.ChosenPriceMinor, "and the new amount of the same winner");
        }

        // (c) The same cheap amount, observed again now: the amount did not change but the price the panel shows
        // really was re-observed, so the honest verdict is an update (spec S9: "unchanged price but new date").
        using (var db = CreateInMemoryContext())
        {
            var provider = new RepricingSteamGameService(db, renewCheapObservationOnly: true);
            var service = new CatalogPriceRefreshService(
                new Repository(db),
                provider,
                new FixedClock(Now),
                NullLogger<CatalogPriceRefreshService>.Instance,
                new CatalogWishlistScopeResolver(new Repository(db)));

            var outcome = service.RefreshPendingAsync(1, CancellationToken.None).GetAwaiter().GetResult();

            CheckEqual(1, outcome.Updated, "the same amount re-observed now is an update");
            CheckEqual(0, outcome.Remaining, "and the game stops being pending");
        }
    }


    private static void HttpContractShape()
    {
        var response = new Deals.API.Controllers.CatalogDealsController.CatalogFreshnessResponse(
            new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc),
            CatalogFreshness.Hours,
            2,
            20,
            31,
            new Deals.API.Controllers.CatalogDealsController.CatalogFreshnessCountsResponse(40, 9, 20, 11),
            2,
            [
                new Deals.API.Controllers.CatalogDealsController.CatalogFreshnessItemResponse(
                    1057800,
                    "Floppy Knights",
                    null,
                    null,
                    "stale",
                    new DateTime(2026, 10, 20, 8, 30, 0, DateTimeKind.Utc),
                    3_000,
                    "Fanatical",
                    "itad",
                    "regional",
                    [
                        new Deals.API.Controllers.CatalogDealsController.CatalogFreshnessProviderResponse(
                            "itad",
                            new DateTime(2026, 10, 20, 8, 30, 0, DateTimeKind.Utc),
                            null)
                    ]),
                // A game the monitor knows but no usable comparable price exists for: every additive field is
                // null, never a zero amount or an invented shop.
                new Deals.API.Controllers.CatalogDealsController.CatalogFreshnessItemResponse(
                    1057801,
                    "Sin precio",
                    null,
                    null,
                    "missing",
                    null,
                    null,
                    null,
                    null,
                    null,
                    [])
            ]);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = document.RootElement;

        foreach (var key in new[]
        {
            "asOfUtc", "freshnessHours", "page", "pageSize", "total", "counts", "unresolvedWishlistEntries",
            "items"
        })
        {
            Check(root.TryGetProperty(key, out _), $"the freshness envelope keeps '{key}'");
        }

        CheckEqual(72, root.GetProperty("freshnessHours").GetInt32(), "the envelope states the 72-hour window");
        CheckEqual(31, root.GetProperty("total").GetInt32(), "total is the selected filter's total");

        // Additive and outside the counters: wishlist entries no exact identity could attribute to a game.
        CheckEqual(2, root.GetProperty("unresolvedWishlistEntries").GetInt32(),
            "the envelope reports the unresolved wishlist entries");
        Check(
            root.GetProperty("counts").TryGetProperty("unresolvedWishlistEntries", out _) == false,
            "the unresolved entries are not folded into the canonical counters");

        var counts = root.GetProperty("counts");
        foreach (var key in new[] { "total", "recent", "stale", "missing" })
        {
            Check(counts.TryGetProperty(key, out _), $"the counters keep '{key}'");
        }

        CheckEqual(40, counts.GetProperty("total").GetInt32(), "counts.total is the whole catalog");

        var item = root.GetProperty("items")[0];
        foreach (var key in new[]
        {
            "gameId", "name", "imageUrl", "steamAppId", "status", "priceObservedAtUtc",
            "chosenPriceMinor", "chosenShopName", "chosenSource", "chosenPricingType", "providers"
        })
        {
            Check(item.TryGetProperty(key, out _), $"a monitor row keeps '{key}'");
        }

        // Additive contract frozen with the UI (user correction S9): the price the panel shows and the fields
        // that explain it come from the SAME winner, never from two different rows.
        CheckEqual(3_000L, item.GetProperty("chosenPriceMinor").GetInt64(),
            "the item reports the chosen cheapest amount in minor units");
        CheckEqual("Fanatical", item.GetProperty("chosenShopName").GetString(),
            "the item reports the shop of the chosen price");
        CheckEqual("itad", item.GetProperty("chosenSource").GetString(),
            "the item reports the source of the chosen price");
        CheckEqual("regional", item.GetProperty("chosenPricingType").GetString(),
            "the item reports the pricing type of the chosen price");
        CheckEqual(
            new DateTime(2026, 10, 20, 8, 30, 0, DateTimeKind.Utc),
            item.GetProperty("priceObservedAtUtc").GetDateTime(),
            "priceObservedAtUtc is the chosen winner's observation instant");

        Check(
            item.GetProperty("imageUrl").ValueKind == JsonValueKind.Null,
            "an unknown image is null, not an empty string");
        Check(
            item.GetProperty("steamAppId").ValueKind == JsonValueKind.Null,
            "a Steam-less game reports a null app id instead of inventing one");

        var provider = item.GetProperty("providers")[0];
        foreach (var key in new[] { "provider", "priceObservedAtUtc", "lastCheckedAtUtc" })
        {
            Check(provider.TryGetProperty(key, out _), $"a provider date keeps '{key}'");
        }
        Check(
            provider.GetProperty("lastCheckedAtUtc").ValueKind == JsonValueKind.Null,
            "an unknown consultation instant is null, never a fabricated attempt");

        var unpriced = root.GetProperty("items")[1];
        foreach (var key in new[] { "priceObservedAtUtc", "chosenPriceMinor", "chosenShopName", "chosenSource", "chosenPricingType" })
        {
            Check(
                unpriced.GetProperty(key).ValueKind == JsonValueKind.Null,
                $"a game with no comparable price reports '{key}' as null, never a zero or invented value");
        }

        // ISO UTC on the wire: the client parses these as instants, so a local or unspecified kind breaks it.
        foreach (var instant in new[]
        {
            root.GetProperty("asOfUtc"),
            item.GetProperty("priceObservedAtUtc"),
            provider.GetProperty("priceObservedAtUtc")
        })
        {
            var text = instant.GetString();
            Check(text is not null && text.EndsWith('Z'), $"'{text}' is serialized as an ISO UTC instant");
        }
    }

    /// <summary>
    /// Offline structural proof that the rendered winner SQL is valid PostgreSQL, not just translatable.
    ///
    /// PostgreSQL rejects nested aggregate calls — &quot;aggregate function calls cannot be nested&quot; — and that
    /// includes an aggregate inside a <c>FILTER (WHERE ...)</c> clause of another aggregate. <c>ToQueryString()</c>
    /// happily renders such a shape, so translation success (the checks above) proves nothing about validity; this
    /// scan does, for EVERY query that embeds the winner selection, not only one of them:
    /// <list type="number">
    /// <item>each <c>FILTER (WHERE ...)</c> clause has its balanced parenthesised expression extracted and is
    /// searched for an aggregate call (<c>min</c>/<c>max</c>/<c>sum</c>/<c>count</c>/<c>avg</c> and the other
    /// aggregates PostgreSQL rejects when nested);</item>
    /// <item>as a superset, every aggregate call found anywhere has its own balanced argument list extracted and is
    /// searched for another aggregate call, so a nested aggregate outside a FILTER cannot pass either.</item>
    /// </list>
    /// Both scans run over a copy with single-quoted literals and comments removed, so a provider name in a
    /// parameter can never be read as an aggregate call, and parentheses are counted for real instead of matched by
    /// a regular expression.
    /// </summary>
    private static void WinnerQueriesAreValidSql()
    {
        using var db = CreateContext();
        var repository = new Repository(db);

        var prices = CatalogFreshnessQueries.PriceUniverse(
            repository.Get<GameOffer>(),
            repository.Get<SteamGame>(),
            Now);
        var states = CatalogFreshnessQueries.GameStates(repository.Get<Game>(), prices, Now);
        var pending = CatalogFreshnessQueries.Pending(states);

        // Every query that embeds the winner selection, rendered separately: a shape can be valid in one of them
        // and invalid in another because each composition nests the same subquery differently.
        (string Name, IQueryable Query)[] winnerQueries =
        [
            ("freshness/price-universe", prices),
            ("freshness/game-states", states),
            ("freshness/counts", CatalogFreshnessQueries.Counts(states)),
            ("freshness/filter/recent", CatalogFreshnessQueries.Filter(states, "recent")),
            ("freshness/page", CatalogFreshnessQueries.Page(states, 20, 20)),
            ("freshness/pending", pending),
            ("freshness/pending-candidates", CatalogFreshnessQueries.PendingCandidates(
                pending, repository.Get<SteamGame>(), CatalogPriceRefreshLimits.BatchSize, Now)),
            ("freshness/pending-not-due", CatalogFreshnessQueries.PendingNotDue(
                pending, repository.Get<SteamGame>(), Now)),
        ];

        foreach (var (name, query) in winnerQueries)
        {
            var sql = Observe(query, name);
            if (sql is null)
            {
                // The translation check already recorded why; validity cannot be asserted over nothing.
                continue;
            }

            var code = StripLiteralsAndComments(sql);

            foreach (var clause in FilterWhereClauses(code))
            {
                var nested = FirstAggregateCall(clause);
                Check(
                    nested is null,
                    $"{name}: the FILTER (WHERE ...) clause calls the aggregate '{nested}', which PostgreSQL rejects " +
                    "with 'aggregate function calls cannot be nested'");
            }

            foreach (var (aggregate, arguments) in AggregateCalls(code))
            {
                var nested = FirstAggregateCall(arguments);
                Check(
                    nested is null,
                    $"{name}: the aggregate '{aggregate}' has the aggregate '{nested}' inside its arguments, which " +
                    "PostgreSQL rejects with 'aggregate function calls cannot be nested'");
            }
        }
    }

    /// <summary>
    /// Names PostgreSQL refuses to nest. The list is the scan's vocabulary, not an SQL parser: it only has to cover
    /// the aggregates a query generator can emit inside another aggregate.
    /// </summary>
    private static readonly string[] AggregateNames =
    [
        "min", "max", "sum", "count", "avg", "every", "bool_and", "bool_or", "string_agg", "array_agg",
        "jsonb_agg", "json_agg",
    ];

    /// <summary>
    /// Removes what must never be read as SQL structure: single-quoted literals (with their <c>''</c> escaping) and
    /// <c>--</c> line comments (the parameter dump EF prints above every query). Parentheses, identifiers and
    /// keywords survive, so the balanced-parenthesis walk below sees the real shape.
    /// </summary>
    private static string StripLiteralsAndComments(string sql)
    {
        var result = new System.Text.StringBuilder(sql.Length);
        var index = 0;

        while (index < sql.Length)
        {
            var current = sql[index];

            if (current == '\'')
            {
                index++;
                while (index < sql.Length)
                {
                    if (sql[index] == '\'')
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == '\'')
                        {
                            index += 2;
                            continue;
                        }

                        index++;
                        break;
                    }

                    index++;
                }

                result.Append("''");
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                result.Append('\n');
                continue;
            }

            result.Append(current);
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// The expression inside every <c>FILTER (WHERE ...)</c>, found by balancing real parentheses rather than by a
    /// pattern: the clause ends where the parenthesis that opened it closes.
    /// </summary>
    private static IEnumerable<string> FilterWhereClauses(string code)
    {
        var searchFrom = 0;

        while (true)
        {
            var filter = code.IndexOf("FILTER", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (filter < 0)
            {
                yield break;
            }

            var cursor = filter + "FILTER".Length;
            while (cursor < code.Length && char.IsWhiteSpace(code[cursor]))
            {
                cursor++;
            }

            if (cursor >= code.Length || code[cursor] != '(')
            {
                searchFrom = cursor;
                continue;
            }

            var close = MatchingParenthesis(code, cursor);
            if (close < 0)
            {
                // Unbalanced SQL is its own failure: report it instead of silently skipping the clause.
                Failures.Add("the rendered SQL has an unbalanced '(' after FILTER");
                yield break;
            }

            yield return code[(cursor + 1)..close];
            searchFrom = close + 1;
        }
    }

    /// <summary>Every aggregate call of the text: its name and the balanced content of its argument list.</summary>
    private static IEnumerable<(string Name, string Arguments)> AggregateCalls(string code)
    {
        foreach (var name in AggregateNames)
        {
            var searchFrom = 0;

            while (searchFrom < code.Length)
            {
                var at = code.IndexOf(name, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }

                var before = at == 0 ? ' ' : code[at - 1];
                var after = at + name.Length;
                var isCall = !char.IsLetterOrDigit(before) && before != '_' ;

                var cursor = after;
                while (cursor < code.Length && char.IsWhiteSpace(code[cursor]))
                {
                    cursor++;
                }

                if (!isCall || cursor >= code.Length || code[cursor] != '(')
                {
                    searchFrom = after;
                    continue;
                }

                var close = MatchingParenthesis(code, cursor);
                if (close < 0)
                {
                    Failures.Add($"the rendered SQL has an unbalanced '(' in an aggregate call to {name}");
                    yield break;
                }

                yield return (name, code[(cursor + 1)..close]);
                searchFrom = close + 1;
            }
        }
    }

    /// <summary>The first aggregate call inside the text, or <c>null</c> when there is none.</summary>
    private static string? FirstAggregateCall(string text) =>
        AggregateCalls(text).Select(call => call.Name).FirstOrDefault();

    /// <summary>Index of the parenthesis closing the one at <paramref name="open"/>, or -1 when unbalanced.</summary>
    private static int MatchingParenthesis(string code, int open)
    {
        var depth = 0;

        for (var index = open; index < code.Length; index++)
        {
            if (code[index] == '(')
            {
                depth++;
            }
            else if (code[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static void EfTranslationIsObserved()
    {
        using var db = CreateContext();
        var repository = new Repository(db);

        var prices = CatalogFreshnessQueries.PriceUniverse(
            repository.Get<GameOffer>(),
            repository.Get<SteamGame>(),
            Now);
        var states = CatalogFreshnessQueries.GameStates(repository.Get<Game>(), prices, Now);

        var universeSql = Observe(prices, "freshness/price-universe");
        var statesSql = Observe(states, "freshness/game-states");
        var countsSql = Observe(CatalogFreshnessQueries.Counts(states), "freshness/counts");
        var recentSql = Observe(CatalogFreshnessQueries.Filter(states, "recent"), "freshness/filter/recent");
        var pageSql = Observe(CatalogFreshnessQueries.Page(states, 20, 20), "freshness/page");
        var pendingSql = Observe(CatalogFreshnessQueries.Pending(states), "freshness/pending");
        var candidatesSql = Observe(
            CatalogFreshnessQueries.PendingCandidates(
                CatalogFreshnessQueries.Pending(states),
                // A fresh queryable on purpose: EF refuses a source reused across two query trees in some shapes.
                repository.Get<SteamGame>(),
                CatalogPriceRefreshLimits.BatchSize,
                Now),
            "freshness/pending-candidates");
        var notDueSql = Observe(
            CatalogFreshnessQueries.PendingNotDue(
                CatalogFreshnessQueries.Pending(states),
                repository.Get<SteamGame>(),
                Now),
            "freshness/pending-not-due");
        var steamRowsSql = Observe(
            CatalogFreshnessQueries.SteamRowsForGames(repository.Get<SteamGame>(), [1L, 2L]),
            "freshness/steam-rows");

        Check(Has(universeSql, "UNION ALL"), "the price universe is one query over provider rows and Steam direct");
        Check(Has(universeSql, "region = "), "the price universe filters the region in SQL");
        Check(Has(universeSql, "observed_at"), "the price universe filters observation dates in SQL");
        Check(Has(universeSql, "observed_at <="), "the price universe refuses future observations in SQL");

        Check(Has(statesSql, "MIN("), "the cheapest usable amount is chosen in SQL");
        Check(Has(statesSql, "MAX("), "and the newest observation among equal cheap amounts is chosen in SQL");
        Check(Has(statesSql, "offer_key"), "the stable provider key breaks the last tie in SQL");
        Check(Has(statesSql, "pricing_type"), "the chosen price carries its own pricing type from SQL");
        Check(Has(statesSql, "shop_name"), "the chosen price carries its own shop from SQL");
        Check(Has(statesSql, "app_id::text"), "the Steam snapshot's key is projected as text in SQL");
        Check(Has(statesSql, "IN ("), "the price universe is narrowed to the selected games before it is grouped");
        Check(Has(statesSql, "games"), "the game states start from the canonical catalog");
        Check(Has(countsSql, "GROUP BY"), "the counters are grouped in SQL");
        Check(Has(recentSql, "WHERE"), "the filter is applied in SQL");
        Check(Has(pageSql, "LIMIT"), "the page is bounded in SQL");
        Check(Has(pageSql, "OFFSET"), "the page skips in SQL");
        Check(Has(pageSql, "ORDER BY"), "the page order is applied in SQL");
        Check(Has(pendingSql, "WHERE"), "pending is filtered in SQL");
        Check(Has(candidatesSql, "JOIN"), "the refresh candidates join the Steam snapshots");
        Check(Has(candidatesSql, "MIN("), "one Steam snapshot per game is chosen in SQL");
        Check(Has(candidatesSql, "LIMIT"), "the candidate batch is bounded in SQL");
        Check(Has(candidatesSql, "observed_at < "),
            "the candidate selection gates the Steam direct snapshot in SQL");
        Check(Has(candidatesSql, "offers_refreshed_at"), "the candidate selection reads the ITAD gate in SQL");
        Check(Has(candidatesSql, "ggdeals_refreshed_at"), "the candidate selection reads the gg.deals gate in SQL");
        Check(Has(notDueSql, "offers_refreshed_at"), "the not-due counter reads the provider gates in SQL");
        Check(Has(steamRowsSql, "offers_refreshed_at"), "the Steam row carries the ITAD consultation stamp");
        Check(Has(steamRowsSql, "epic_refreshed_at") && Has(steamRowsSql, "microsoft_refreshed_at"),
            "the Steam row carries every direct-store consultation stamp");
        Check(Has(steamRowsSql, "= ANY"), "the page's game ids are filtered in SQL");
    }

    // --- In-memory fixture for the measured post-refresh behaviour ------------------------------

    /// <summary>
    /// The real services over the EF in-memory provider: the monitor and the pass run exactly as they do in
    /// production (no connection is opened), so "is a fresh Steam price counted as the game's price" is an
    /// observation instead of a promise.
    /// </summary>
    private static AppDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"catalog-freshness-checks-{Guid.NewGuid():N}")
            .Options;

        var db = new AppDbContext(options, new OfflineCurrentUserService());

        db.Games.Add(GameRow(1, "Floppy Knights"));
        db.UserLibrary.Add(new UserLibrary
        {
            UserLibraryId = 1,
            UserId = 1,
            GameId = 1,
            Store = "steam",
            StoreGameId = "100",
            Title = "Floppy Knights",
            State = LibraryStates.Wished,
            ImportedAt = Now.AddDays(-30),
        });

        // 399.00 MXN on Steam, ten days old, with a due consultation stamp for every phase: the pass has a
        // real reason to ask again, which is what makes the false-update measurement observable.
        db.SteamGames.Add(new SteamGame
        {
            SteamGameId = 41,
            AppId = 100,
            GameId = 1,
            Region = "mx",
            Currency = "MXN",
            Name = "Floppy Knights",
            InitialPriceMinor = 39_900,
            CurrentPriceMinor = 39_900,
            ObservedAt = Now.AddDays(-10),
            OffersRefreshedAt = Now.AddDays(-10),
            GgDealsRefreshedAt = Now.AddDays(-10),
            EpicRefreshedAt = Now.AddDays(-10),
            MicrosoftRefreshedAt = Now.AddDays(-10),
        });

        // 30.00 MXN from ITAD, ten days old: the price the panel must show.
        db.GameOffers.Add(Offer(1, Now.AddDays(-10), price: 3_000, originalPrice: 3_000));

        db.SaveChanges();
        return db;
    }

    /// <summary>
    /// Provider double for the measured pass: it writes what a real refresh writes (an authoritative, persisted
    /// answer) and nothing else. By default it only renews the Steam snapshot's own price date — the exact case
    /// where a naive "newest observation" measurement would claim an update. With a new cheap amount it
    /// reprices the cheap offer itself, which is the positive control.
    /// </summary>
    private sealed class RepricingSteamGameService(
        AppDbContext db,
        int? newCheapPriceMinor = null,
        bool renewCheapObservationOnly = false)
        : ISteamGameService
    {
        public List<int> Calls { get; } = [];

        public Task<SteamStalePriceRefresh> RefreshStalePricesAsync(
            int appId,
            CancellationToken cancellationToken,
            int ageHours = GameRefreshPolicy.StalePriceBatchHours)
        {
            Calls.Add(appId);

            var snapshot = db.SteamGames.Single(game => game.AppId == appId);
            snapshot.ObservedAt = Now;
            snapshot.OffersRefreshedAt = Now;
            snapshot.GgDealsRefreshedAt = Now;
            snapshot.EpicRefreshedAt = Now;
            snapshot.MicrosoftRefreshedAt = Now;

            if (newCheapPriceMinor is { } price)
            {
                var cheap = db.GameOffers.Single(offer => offer.GameId == snapshot.GameId);
                cheap.MxnCurrentPriceMinor = price;
                cheap.MxnRegularPriceMinor = price;
                cheap.OriginalCurrentPriceMinor = price;
                cheap.OriginalRegularPriceMinor = price;
                cheap.ObservedAt = Now;
            }
            else if (renewCheapObservationOnly)
            {
                // The same amount, observed again now: no amount changed, only the date the panel shows.
                db.GameOffers.Single(offer => offer.GameId == snapshot.GameId).ObservedAt = Now;
            }

            db.SaveChanges();

            return Task.FromResult(new SteamStalePriceRefresh(
                Ran: true,
                SteamDue: true,
                DuePhases: [SteamStalePricePhases.Steam, SteamStalePricePhases.Itad],
                AdvancedPhases: [SteamStalePricePhases.Steam, SteamStalePricePhases.Itad],
                DegradedPhases: []));
        }

        public Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the price pass never searches");

        public Task<SteamGameDetails?> GetByAppIdAsync(
            int appId,
            bool forceRefresh,
            CancellationToken cancellationToken,
            bool interactive = false) =>
            throw new NotSupportedException("the price pass never loads details directly");

        public Task<SteamGameDetails?> GetAppDetailsOnlyAsync(int appId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the price pass never loads Steam-only details");

        public Task<SteamGameDetails?> RefreshProvidersAsync(int appId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the price pass uses its own 72 h entry point");

        public Task<IReadOnlyList<SteamSearchResult>> GetSuggestionsAsync(
            string? query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("the price pass never suggests");

        public Task<IReadOnlyList<SteamDiscoverItem>> GetDiscoverAsync(
            string list,
            int page,
            int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("the price pass never reads discovery lists");
    }

    /// <summary>Fixed clock: every instant of the fixture is expressed relative to one reference.</summary>
    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=offline-checks;Port=1;Database=deals_checks;Username=checks;Password=checks;Timeout=1")
            .Options;

        return new AppDbContext(options, new OfflineCurrentUserService());
    }

    private static string? Observe(IQueryable query, string name)
    {
        try
        {
            var sql = query.ToQueryString();
            if (Environment.GetEnvironmentVariable("FRESHNESS_SQL_DUMP") == "1")
            {
                Console.WriteLine($"=== {name} ===\n{sql}\n");
            }

            Check(!string.IsNullOrWhiteSpace(sql), $"EF renders non-empty SQL for {name}");
            return sql;
        }
        catch (Exception exception)
        {
            Failures.Add($"EF translation FAILED for {name}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static bool Has(string? sql, string marker) =>
        sql is not null && sql.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private sealed class OfflineCurrentUserService : ICurrentUserService
    {
        public int? GetUserId() => null;

        public int GetRequiredUserId() => throw new InvalidOperationException("offline checks have no user");

        public int? GetSessionVersion() => null;

        public Guid? GetSessionId() => null;

        public string GetName() => "freshness-checks";

        public string GetEmail() => "freshness-checks@example.test";

        public bool IsAdmin() => false;
    }
}
