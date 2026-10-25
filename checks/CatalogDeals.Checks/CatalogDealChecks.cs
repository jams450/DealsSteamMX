using System.Globalization;
using System.Reflection;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Behaviour checks for the persisted discover catalog: the shared policy boundary, the SQL predicates and
/// queries the catalog composes, the per-category selection rules, the one-row card contract, and exact parity
/// with the notifier's candidate mapping.
///
/// Everything runs offline. The predicates are exercised through <c>Expression.Compile</c> over in-memory
/// entities, and the queries themselves are rendered with <c>ToQueryString()</c> against a real
/// <see cref="AppDbContext"/> built on a <c>UseNpgsql</c> provider with a dummy connection string: rendering
/// translates the query with the production provider and never opens a connection, so a translation failure is
/// observed here instead of discovered as a 500 in production.
///
/// The three tabs are asserted to be three independent OR rules (spec L1/L2): a game can appear in `historic`
/// while costing more than the cheap threshold, and in `score` without being cheap at all.
/// </summary>
internal static class CatalogDealChecks
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromHours(72);
    private static readonly DealAlertContext Context = new(Now, Window, 50);
    private static readonly List<string> Failures = [];

    public static void Run()
    {
        CategoryTokens();
        PagerBounds();
        SharedCheapThreshold();
        ComparablePredicateMatchesPolicy();
        CheapPricePredicateMatchesPolicy();
        HistoryPredicateMatchesPolicy();
        ScorePrefilterIsNecessaryOnly();
        ScorePrefilterDoesNotOverflow();
        CategoriesAreIndependent();
        EligibilityIsDecidedBeforeBestOffer();
        OneCardPerGameWithDeterministicTie();
        CardFieldsComeFromOneRow();
        SteamDirectAndSteamLessRules();
        ScoreContextIsTheSharedOne();
        SteamContextIsKeyedByTheExactSnapshot();
        HistoryLowIsKeyedByTheExactSteamSnapshot();
        NotifierCandidateParity();
        EfTranslationIsObserved();

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: {Failures.Count} catalog deal checks failed");
            foreach (var failure in Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            Environment.Exit(1);
        }

        Console.WriteLine("PASS: catalog deal discover checks");
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

    // --- Fixtures -----------------------------------------------------------

    private static CatalogDealOfferRow Row(
        long gameId = 100,
        int? steamGameId = 42,
        string source = "itad",
        string offerKey = "offer",
        string shop = "Shop",
        string classification = "authorized",
        string pricing = "regional",
        string originalCurrency = "MXN",
        int? originalPrice = null,
        int price = 4_999,
        int? historyLow = null,
        string? historyCurrency = null,
        DateTime? observedAt = null,
        string? dealUrl = "https://example.test/deal") =>
        new()
        {
            GameId = gameId,
            SteamGameId = steamGameId,
            Source = source,
            OfferKey = offerKey,
            ShopName = shop,
            Classification = classification,
            PricingType = pricing,
            OriginalCurrency = originalCurrency,
            OriginalCurrentPriceMinor = originalPrice ?? price,
            MxnCurrentPriceMinor = price,
            HistoryLowAllMinor = historyLow,
            HistoryLowCurrency = historyCurrency,
            ObservedAt = observedAt ?? Now,
            DealUrl = dealUrl,
        };

    private static Game GameRow(long gameId = 100, string title = "Floppy Knights", string? image = "https://img.test/1.png") =>
        new()
        {
            GameId = gameId,
            Title = title,
            NormalizedTitle = title,
            ImageUrl = image,
        };

    private static SteamGame Steam(
        int steamGameId = 42,
        int appId = 1_057_800,
        int current = 4_999,
        int? initial = 100_000,
        string currency = "MXN",
        string region = "mx",
        long? gameId = 100,
        DateTime? observedAt = null,
        string name = "Floppy Knights") =>
        new()
        {
            SteamGameId = steamGameId,
            AppId = appId,
            Name = name,
            Region = region,
            Currency = currency,
            InitialPriceMinor = initial,
            CurrentPriceMinor = current,
            ObservedAt = observedAt ?? Now,
            GameId = gameId,
        };

    /// <summary>
    /// Evaluates one offer row through the shared policy, giving the row the Steam snapshot its own
    /// <see cref="CatalogDealOfferRow.SteamGameId"/> names — exactly what the service does.
    /// </summary>
    private static DealAlertDecision Decide(
        CatalogDealOfferRow row,
        int? basePrice = 100_000,
        string? baseCurrency = "MXN",
        int? mxnLow = null,
        int? steamAppId = 1_057_800)
    {
        var steam = row.SteamGameId is { } steamGameId
            ? Steam(steamGameId, appId: steamAppId ?? 1_057_800, initial: basePrice, currency: baseCurrency ?? "MXN", gameId: row.GameId)
            : null;
        var game = GameRow(row.GameId);

        return DealAlertPolicy.Evaluate(CatalogDealSelection.ToCandidate(row, steam, game, mxnLow), Context);
    }

    private static bool In(DealAlertDecision decision, string category) =>
        CatalogDealSelection.IsInCategory(decision, category);

    // --- Category tokens and paging -----------------------------------------

    private static void CategoryTokens()
    {
        CheckEqual(3, CatalogDealCategories.All.Count, "three stable category tokens");
        CheckEqual("under50,historic,score", string.Join(",", CatalogDealCategories.All), "token order and spelling");
        CheckEqual(CatalogDealCategories.Under50, CatalogDealCategories.Parse("under50"), "parse under50");
        CheckEqual(CatalogDealCategories.Historic, CatalogDealCategories.Parse(" HISTORIC "), "parse trims and lowercases");
        CheckEqual(CatalogDealCategories.Score, CatalogDealCategories.Parse("Score"), "parse score");

        foreach (var rejected in new[] { "discount", "recent", "", "under-50", null })
        {
            try
            {
                CatalogDealCategories.Parse(rejected);
                Failures.Add($"unknown category '{rejected}' must be rejected");
            }
            catch (ArgumentException exception)
            {
                Check(exception.Message.Contains("under50, historic, score", StringComparison.Ordinal),
                    "rejection lists the valid tokens");
            }
        }
    }

    private static void PagerBounds()
    {
        CheckEqual(1, CatalogDealPager.NormalizePage(0), "page 0 normalizes to 1");
        CheckEqual(1, CatalogDealPager.NormalizePage(-3), "negative page normalizes to 1");
        CheckEqual(7, CatalogDealPager.NormalizePage(7), "valid page kept");
        CheckEqual(CatalogDealPager.MaxPage, CatalogDealPager.NormalizePage(int.MaxValue), "page clamped");
        CheckEqual(CatalogDealPager.DefaultPageSize, CatalogDealPager.NormalizePageSize(0), "pageSize 0 -> default");
        CheckEqual(CatalogDealPager.DefaultPageSize, CatalogDealPager.NormalizePageSize(-1), "negative pageSize -> default");
        CheckEqual(3, CatalogDealPager.NormalizePageSize(3), "valid pageSize kept");
        CheckEqual(CatalogDealPager.MaxPageSize, CatalogDealPager.NormalizePageSize(10_000), "pageSize clamped");
        Check(CatalogDealPager.MaxPageSize <= 100, "page size stays bounded");
    }

    // --- The shared policy threshold ----------------------------------------

    /// <summary>The cheap rule is shared with the notifier and it is strict: 4999 fires, 5000 does not.</summary>
    private static void SharedCheapThreshold()
    {
        CheckEqual(5_000, DealAlertPolicy.CheapPriceThresholdMinor, "shared cheap threshold is 5000 minor");

        var below = Decide(Row(price: 4_999), basePrice: 100_000);
        Check(below.Triggered, "4999 fires");
        Check(below.Reasons.Contains(DealAlertReasons.Price), "4999 fires by price");

        var atBoundary = Decide(Row(price: 5_000), basePrice: 100_000);
        Check(!atBoundary.Reasons.Contains(DealAlertReasons.Price), "5000 does not fire by price");
        CheckEqual(7d, atBoundary.Score, "5000 with a 95% discount scores exactly 7.0");
        Check(!atBoundary.Triggered, "5000 with nothing else does not fire");

        var priceFilter = CatalogDealQuery.CheapPrice().Compile();
        Check(priceFilter(Offer(price: 4_999)), "price filter admits 4999");
        Check(!priceFilter(Offer(price: 5_000)), "price filter rejects 5000");

        var steamFilter = CatalogDealQuery.CheapPriceSteam().Compile();
        Check(steamFilter(Steam(current: 4_999)), "steam price filter admits 4999");
        Check(!steamFilter(Steam(current: 5_000)), "steam price filter rejects 5000");
    }

    // --- SQL predicates mirror the shared policy ----------------------------

    private static GameOffer Offer(
        int? price = 4_999,
        string source = "itad",
        string pricing = "regional",
        int? original = 4_999,
        string currency = "MXN",
        string region = "mx",
        long? gameId = 100,
        DateTime? observedAt = null,
        int? historyLow = null,
        string? historyCurrency = null,
        int? steamGameId = 42) =>
        new()
        {
            GameOfferId = 1,
            SteamGameId = steamGameId,
            GameId = gameId,
            Region = region,
            Source = source,
            OfferKey = "offer",
            ShopName = "Shop",
            Classification = "authorized",
            OriginalCurrency = currency,
            OriginalCurrentPriceMinor = original,
            MxnCurrentPriceMinor = price,
            HistoryLowAllMinor = historyLow,
            HistoryLowCurrency = historyCurrency,
            PricingType = pricing,
            DealUrl = "https://example.test/deal",
            ObservedAt = observedAt ?? Now,
        };

    /// <summary>
    /// The reader's mapping is the one the policy consumes; only the fields the shared gate reads are set
    /// here (price, pricing type, source, original price, region, observation instant).
    /// </summary>
    private static DealAlertCandidate CandidateOf(GameOffer offer) =>
        new(
            offer.GameId,
            offer.SteamGameId,
            "Floppy Knights",
            offer.Source,
            offer.OfferKey,
            offer.ShopName,
            offer.Classification,
            offer.PricingType,
            offer.Region,
            offer.OriginalCurrency,
            offer.OriginalCurrentPriceMinor,
            offer.MxnCurrentPriceMinor ?? 0,
            offer.HistoryLowAllMinor,
            offer.HistoryLowCurrency,
            offer.ObservedAt,
            null,
            null,
            null,
            null,
            offer.DealUrl);

    /// <summary>
    /// The shared gate, row by row: <see cref="DealAlertPolicy.IsComparable"/> is exactly
    /// <c>ComparableOffer</c> plus <see cref="DealAlertPolicy.Region"/>, and <c>CanonicalOffer</c> is exactly the
    /// shared gate plus the canonical anchor. The region dimension is swept too (foreign, uppercase, blank,
    /// padded), because the notifier and the catalog disagreeing there is the defect this pins: the notifier used
    /// to push a foreign row converted under 50 MXN that the MX-only catalog would never show.
    /// </summary>
    private static void ComparablePredicateMatchesPolicy()
    {
        var comparable = CatalogDealQuery.ComparableOffer(Now, Window).Compile();
        var canonical = CatalogDealQuery.CanonicalOffer(Now, Window).Compile();

        int?[] prices = [null, 0, -1, 1, 4_999, 5_000, DealAlertPolicy.MaxPriceMinor, DealAlertPolicy.MaxPriceMinor + 1];
        string[] pricing = ["regional", "fx_estimate", "unconverted"];
        string[] sources = ["itad", "epic", "steam", "microsoft"];
        int?[] originals = [null, 0, 1_000];
        string[] regions = ["mx", "MX", "us", "", " mx"];
        DateTime[] observed =
        [
            Now,
            Now - Window,
            Now - Window - TimeSpan.FromHours(1),
            Now + TimeSpan.FromSeconds(1),
        ];

        foreach (var price in prices)
        foreach (var pricingType in pricing)
        foreach (var source in sources)
        foreach (var original in originals)
        foreach (var region in regions)
        foreach (var observedAt in observed)
        {
            var offer = Offer(price, source, pricingType, original, region: region, observedAt: observedAt);
            var policy = DealAlertPolicy.IsComparable(CandidateOf(offer), Context);
            var sql = comparable(offer);

            // The shared gate is the catalog's comparability predicate PLUS the MX region, with the catalog's
            // exact lowercase convention: `region = 'mx'` in SQL, DealAlertPolicy in the notifier. So a row the
            // notifier would push is exactly a row the catalog can show.
            CheckEqual(sql && offer.Region == CatalogDealQuery.Region, policy,
                $"shared gate = comparable + mx region: price={price?.ToString() ?? "null"} pricing={pricingType} " +
                $"source={source} original={original?.ToString() ?? "null"} region='{region}' observed={observedAt:O} " +
                $"expected {sql && offer.Region == CatalogDealQuery.Region} got {policy}");

            // The catalog adds exactly one requirement on top of the shared gate: a canonical game anchor.
            // It never relaxes the region or the freshness/comparability rules.
            CheckEqual(policy && offer.GameId != null, canonical(offer),
                $"canonical predicate is the shared gate plus the canonical anchor: price={price?.ToString() ?? "null"} " +
                $"region='{region}' gameId={offer.GameId?.ToString() ?? "null"}");
        }

        CheckEqual("mx", CatalogDealQuery.Region, "the catalog region token is the lowercase mx the policy requires");
        CheckEqual(CatalogDealQuery.Region, DealAlertPolicy.Region,
            "catalog SQL and the shared policy compare the same region token");
        Check(!canonical(Offer(price: 4_999, region: "us")), "non-MX region is not catalog eligible");
        Check(!canonical(Offer(price: 4_999, region: "MX")), "an uppercase region is not catalog eligible either");
        Check(!canonical(Offer(price: 4_999, region: "")), "a blank region is not catalog eligible");
        Check(!canonical(Offer(price: 4_999, gameId: null)), "offer without canonical game id is not eligible");

        // The same three negatives through the shared policy, which is what the notifier calls.
        Check(DealAlertPolicy.IsComparable(CandidateOf(Offer(price: 4_999)), Context), "mx region: comparable");
        Check(!DealAlertPolicy.IsComparable(CandidateOf(Offer(price: 4_999, region: "us")), Context),
            "the shared policy rejects a foreign region");
        Check(!DealAlertPolicy.IsComparable(CandidateOf(Offer(price: 4_999, region: "MX")), Context),
            "the shared policy rejects an uppercase region (exact lowercase convention)");
        Check(!DealAlertPolicy.IsComparable(CandidateOf(Offer(price: 4_999, region: "")), Context),
            "the shared policy rejects a blank region");
        Check(!DealAlertPolicy.IsComparable(CandidateOf(Offer(price: 4_999, region: " mx")), Context),
            "the shared policy never trims a region");
    }

    private static void CheapPricePredicateMatchesPolicy()
    {
        var filter = CatalogDealQuery.CheapPrice().Compile();
        foreach (var price in new int?[] { null, 0, 1, 4_998, 4_999, 5_000, 5_001, 100_000 })
        {
            var offer = Offer(price);
            var policy = DealAlertPolicy.Evaluate(CandidateOf(offer), Context).Reasons.Contains(DealAlertReasons.Price);
            CheckEqual(policy, filter(offer) && DealAlertPolicy.IsComparable(CandidateOf(offer), Context),
                $"price predicate vs policy at price={price?.ToString() ?? "null"}");
        }
    }

    private static void HistoryPredicateMatchesPolicy()
    {
        var filter = CatalogDealQuery.VerifiedHistoryLow().Compile();
        string[] sources = ["itad", "ggdeals", "steam", "epic"];
        string[] currencies = ["MXN", "USD"];
        int?[] originals = [null, 0, 6_000];
        int?[] lows = [null, 0, 5_000, 5_999, 6_000, 6_001];
        string?[] lowCurrencies = [null, "", "MXN", "USD"];

        foreach (var source in sources)
        foreach (var currency in currencies)
        foreach (var original in originals)
        foreach (var low in lows)
        foreach (var lowCurrency in lowCurrencies)
        {
            var offer = Offer(
                price: 6_000,
                source: source,
                original: original,
                currency: currency,
                historyLow: low,
                historyCurrency: lowCurrency);
            var policy = DealAlertPolicy.Evaluate(CandidateOf(offer), Context).Reasons.Contains(DealAlertReasons.HistoryLow);
            CheckEqual(policy, filter(offer),
                $"history predicate vs policy: source={source} currency={currency} " +
                $"original={original?.ToString() ?? "null"} low={low?.ToString() ?? "null"} " +
                $"lowCurrency={lowCurrency ?? "null"}");
        }

        Check(!filter(Offer(source: "steam", original: 3_000, historyLow: 3_000, historyCurrency: "MXN")),
            "a Steam row can never be a provider history low");
    }

    /// <summary>
    /// The score tab is the only rule that cannot be answered from the offer row alone (it needs the MXN
    /// Steam base and the provider MXN low), so the SQL pre-filter is only allowed to be a superset.
    /// This asserts necessity and shows the filter is not used as a substitute for the policy.
    /// </summary>
    private static void ScorePrefilterIsNecessaryOnly()
    {
        int[] bases = [0, 1_000, 10_000, 100_000, 300_000];
        int[] prices = [0, 1, 999, 4_999, 5_000, 15_000, 50_000, 99_999];
        int?[] lows = [null, 0, 1, 2_500, 5_000];

        var supersetSeen = false;
        foreach (var basePrice in bases)
        foreach (var price in prices)
        foreach (var low in lows)
        {
            var score = DealAlertPolicy.DealScore(basePrice, "MXN", price, low, low is null ? null : "MXN", 50);
            var necessary = CatalogDealQuery.ScoreNeedsMoreThanHalfOff(price, basePrice);
            if (score is > DealAlertPolicy.ScoreTriggerThreshold)
            {
                Check(necessary,
                    $"score pre-filter must be necessary: base={basePrice} price={price} low={low?.ToString() ?? "null"} score={score}");
            }
            else if (score is not null && necessary)
            {
                supersetSeen = true;
            }
        }

        Check(supersetSeen, "the score pre-filter is a superset, never the score rule itself");
    }

    /// <summary>
    /// The necessary filter multiplies a price by two. In <c>int</c> that overflows for a corrupt snapshot and
    /// wraps negative, which would read as "more than half off" for every such row.
    /// </summary>
    private static void ScorePrefilterDoesNotOverflow()
    {
        Check(!CatalogDealQuery.ScoreNeedsMoreThanHalfOff(int.MaxValue, int.MaxValue),
            "int.MaxValue price against an int.MaxValue base is not more than half off");
        Check(!CatalogDealQuery.ScoreNeedsMoreThanHalfOff(int.MaxValue, 200_000_000),
            "int.MaxValue price against 200,000,000 is not more than half off");
        Check(!CatalogDealQuery.ScoreNeedsMoreThanHalfOff(DealAlertPolicy.MaxPriceMinor, DealAlertPolicy.MaxPriceMinor),
            "the comparability ceiling price against its own base is not more than half off");
        Check(CatalogDealQuery.ScoreNeedsMoreThanHalfOff(4_999, 10_001), "4999 against 10001 is more than half off");

        var steamFilter = CatalogDealQuery.ScoreNeedsMoreThanHalfOffSteam().Compile();
        Check(!steamFilter(Steam(current: int.MaxValue, initial: int.MaxValue)),
            "the Steam direct pre-filter does not overflow either");
        Check(steamFilter(Steam(current: 4_999, initial: 10_001)), "the Steam direct pre-filter admits a real discount");
    }

    // --- Independent categories --------------------------------------------

    private static void CategoriesAreIndependent()
    {
        var cheap = Decide(Row(price: 4_999, source: "epic", offerKey: "cheap"), basePrice: 100_000);
        Check(In(cheap, CatalogDealCategories.Under50), "cheap offer is in under50");
        Check(!In(cheap, CatalogDealCategories.Historic), "cheap offer without history is not historic");
        Check(!In(cheap, CatalogDealCategories.Score), "cheap offer scoring 7.0 is not in score");

        var historic = Decide(
            Row(price: 6_000, originalPrice: 6_000, historyLow: 6_000, historyCurrency: "MXN"),
            basePrice: 10_000);
        Check(!In(historic, CatalogDealCategories.Under50), "60.00 MXN historic offer is not under50");
        Check(In(historic, CatalogDealCategories.Historic), "historic offer is historic");
        Check(!In(historic, CatalogDealCategories.Score), "historic offer scoring 3.0 is not in score");

        // No ceiling on the score rule: a 150.00 MXN offer qualifies on its score alone.
        var scored = Decide(
            Row(price: 15_000, originalPrice: 15_000),
            basePrice: 300_000,
            mxnLow: 7_500);
        Check(!In(scored, CatalogDealCategories.Under50), "scored offer is not under50");
        Check(!In(scored, CatalogDealCategories.Historic), "scored offer is not historic");
        Check(In(scored, CatalogDealCategories.Score), "scored offer is in score");
        CheckEqual(8.5d, scored.Score, "scored offer uses the shared formula");

        var allThree = Decide(
            Row(price: 1_000, originalPrice: 1_000, historyLow: 1_000, historyCurrency: "MXN"),
            basePrice: 20_000,
            mxnLow: 100);
        Check(allThree.Reasons.SequenceEqual(
                [DealAlertReasons.Price, DealAlertReasons.Score, DealAlertReasons.HistoryLow]),
            "an offer can satisfy all three rules in the shared reason order");
        foreach (var category in CatalogDealCategories.All)
        {
            Check(In(allThree, category), $"an offer satisfying all three rules appears in {category}");
        }
    }

    /// <summary>
    /// The measured failure mode this prevents: choosing the cheapest offer first and then asking whether
    /// it is historic hides the game from the historic tab even though a qualifying historic offer exists.
    /// </summary>
    private static void EligibilityIsDecidedBeforeBestOffer()
    {
        var cheap = Decide(Row(price: 1_000, source: "epic", offerKey: "cheap"), basePrice: 100_000);
        var historic = Decide(
            Row(price: 6_000, source: "itad", offerKey: "historic", originalPrice: 6_000,
                historyLow: 6_000, historyCurrency: "MXN"),
            basePrice: 10_000);

        var historicCards = CatalogDealSelection.BestPerGame([cheap, historic], CatalogDealCategories.Historic);
        CheckEqual(1, historicCards.Count, "a game with one qualifying historic offer still gets a card");
        CheckEqual("historic", historicCards[0].Candidate.OfferKey, "the historic card is the qualifying offer");
        CheckEqual(6_000, historicCards[0].Candidate.MxnCurrentPriceMinor, "the historic card keeps its own price");

        var cheapCards = CatalogDealSelection.BestPerGame([cheap, historic], CatalogDealCategories.Under50);
        CheckEqual(1, cheapCards.Count, "the under50 tab keeps the cheap card");
        CheckEqual("cheap", cheapCards[0].Candidate.OfferKey, "the under50 card is the cheap offer");

        var both = CatalogDealSelection.BestPerGame([cheap, historic], CatalogDealCategories.Historic);
        Check(!both.Any(card => card.Candidate.OfferKey == "cheap"),
            "a non-qualifying offer never becomes a category card");
    }

    private static void OneCardPerGameWithDeterministicTie()
    {
        var first = Decide(Row(gameId: 7, price: 4_000, source: "epic", offerKey: "b"), basePrice: 100_000);
        var second = Decide(Row(gameId: 7, price: 4_000, source: "itad", offerKey: "a"), basePrice: 100_000);
        var third = Decide(Row(gameId: 8, price: 3_000, source: "itad", offerKey: "c"), basePrice: 100_000);

        var cards = CatalogDealSelection.BestPerGame([first, second, third], CatalogDealCategories.Under50);
        CheckEqual(2, cards.Count, "one card per game");
        CheckEqual(8L, cards[0].Candidate.GameId, "cards are ordered by best price then game id");
        CheckEqual(7L, cards[1].Candidate.GameId, "the tied game follows the cheaper one");

        // Equal price: the ordinal-smallest source then offer key wins, whatever the input order.
        var tieForGame7 = cards[1];
        CheckEqual("epic", tieForGame7.Candidate.Source, "tie-break prefers the ordinal-smallest source");
        var reversed = CatalogDealSelection.BestPerGame([third, second, first], CatalogDealCategories.Under50);
        CheckEqual(
            string.Join("|", cards.Select(card => $"{card.Candidate.GameId}:{card.Candidate.Source}:{card.Candidate.OfferKey}")),
            string.Join("|", reversed.Select(card => $"{card.Candidate.GameId}:{card.Candidate.Source}:{card.Candidate.OfferKey}")),
            "selection does not depend on the input order");
    }

    /// <summary>
    /// The mislabelling failure this prevents (AGENTS.md, four shipped versions of it): a card must never
    /// paint one store's name over another store's price or link.
    /// </summary>
    private static void CardFieldsComeFromOneRow()
    {
        var epic = Decide(
            Row(price: 4_999, source: "epic", shop: "Epic Games Store", classification: "official",
                pricing: "regional", dealUrl: "https://epic.test/1"),
            basePrice: 100_000);
        var steam = Decide(
            Row(price: 4_199, source: "itad", shop: "GamesPlanet", classification: "authorized",
                pricing: "fx_estimate", originalCurrency: "USD", originalPrice: 399,
                dealUrl: "https://itad.test/2"),
            basePrice: 100_000);

        var best = CatalogDealSelection.BestPerGame([epic, steam], CatalogDealCategories.Under50);
        CheckEqual(1, best.Count, "one card for the game");
        var card = CatalogDealSelection.ToItem(best[0], "Floppy Knights", "https://img.test/1.png", 1_057_800);

        CheckEqual("itad", card.Source, "card source follows the winning row");
        CheckEqual("GamesPlanet", card.ShopName, "card shop follows the winning row");
        CheckEqual("fx_estimate", card.PricingType, "card pricing type follows the winning row");
        CheckEqual("https://itad.test/2", card.DealUrl, "card url follows the winning row");
        CheckEqual(4_199, card.MxnPriceMinor, "card price follows the winning row");
        CheckEqual(399, card.OriginalPriceMinor, "card keeps the native quote of the winning row");
        CheckEqual("USD", card.OriginalCurrency, "card keeps the native currency of the winning row");
        Check(card.UsesFxEstimate, "card labels an FX estimate");
        CheckEqual(1_057_800, card.SteamAppId, "card exposes the nullable steam app id");
        CheckEqual("Floppy Knights", card.Title, "card title");
        CheckEqual("https://img.test/1.png", card.ImageUrl, "card image");
        Check(card.Reasons.Contains(DealAlertReasons.Price), "card carries the shared reasons");
        CheckEqual(Now, card.ObservedAt, "card carries the offer observation instant");
    }

    private static void SteamDirectAndSteamLessRules()
    {
        // A Steam direct row never claims a history low, even when its own local minimum exists.
        var steamDirect = Decide(Row(source: "steam", shop: "Steam", price: 4_000, originalPrice: 4_000));
        Check(In(steamDirect, CatalogDealCategories.Under50), "Steam direct qualifies under50");
        Check(!In(steamDirect, CatalogDealCategories.Historic), "Steam direct never qualifies as historic");
        CheckEqual("1057800", steamDirect.Candidate.OfferKey, "Steam direct offer key is the app id");

        var steamDirectScored = Decide(Row(source: "steam", shop: "Steam", price: 5_000), basePrice: 100_000, mxnLow: 2_500);
        Check(In(steamDirectScored, CatalogDealCategories.Score), "Steam direct may qualify by score");

        // A Steam-less game links to the provider URL it carries, never to an invented appid route.
        var steamLess = Decide(
            Row(source: "microsoft", shop: "Xbox", steamGameId: null, price: 4_000,
                dealUrl: "https://xbox.test/game"),
            basePrice: null,
            baseCurrency: null,
            steamAppId: null);
        Check(In(steamLess, CatalogDealCategories.Under50), "Steam-less game qualifies under50");
        CheckEqual(4_000, steamLess.Candidate.MxnCurrentPriceMinor, "Steam-less card keeps its own price");
        var card = CatalogDealSelection.ToItem(steamLess, steamLess.Candidate.GameName, null, null);
        CheckEqual(null, card.SteamAppId, "Steam-less card has no app id");
        CheckEqual("https://xbox.test/game", card.DealUrl, "Steam-less card keeps the provider url");

        // No base price and no provider low means no score: nothing is fabricated.
        Check(!In(steamLess, CatalogDealCategories.Score), "Steam-less game cannot score without an MXN base");

        // Defensive: a Steam direct row without a url resolves to the store page of its own app id, and a
        // provider row without a url resolves to nothing rather than a guessed link.
        CheckEqual("https://store.steampowered.com/app/1057800/",
            CatalogDealSelection.ResolveDealUrl(Row(source: "steam", dealUrl: null), 1_057_800),
            "Steam direct url comes from its own app id");
        CheckEqual(null, CatalogDealSelection.ResolveDealUrl(Row(source: "itad", dealUrl: null), 1_057_800),
            "provider row without url never invents a link");
    }

    /// <summary>
    /// The score shown on a card is the shared policy score computed from the same context the notifier
    /// feeds it: the MXN Steam base and the provider-reported MXN history low.
    /// </summary>
    private static void ScoreContextIsTheSharedOne()
    {
        var row = Row(price: 5_000, originalPrice: 5_000);

        var withLow = Decide(row, basePrice: 100_000, mxnLow: 2_500);
        CheckEqual(
            DealAlertPolicy.DealScore(100_000, "MXN", 5_000, 2_500, "MXN", 50),
            withLow.Score,
            "card score is the shared formula over the shared inputs");

        var foreignBase = Decide(row, basePrice: 100_000, baseCurrency: "USD", mxnLow: 2_500);
        Check(foreignBase.Score is null, "a non-MXN base yields no score instead of a fabricated one");
        Check(!In(foreignBase, CatalogDealCategories.Score), "no score reason without a comparable base");

        var noLow = Decide(row, basePrice: 100_000);
        CheckEqual(7d, noLow.Score, "missing provider low leaves only the discount half of the formula");
        Check(!In(noLow, CatalogDealCategories.Score), "7.0 is not greater than 7");
    }

    /// <summary>
    /// A game can have more than one Steam snapshot. The base price, the currency and the card identity must
    /// come from the row's own <c>steam_game_id</c>, never from "the game's first Steam row": the defect this
    /// pins made a card score against a different app id than the notifier did.
    /// </summary>
    private static void SteamContextIsKeyedByTheExactSnapshot()
    {
        var game = GameRow(700);
        var firstSnapshot = Steam(steamGameId: 10, appId: 111, initial: 100_000, gameId: 700, name: "Floppy Knights");
        var ownSnapshot = Steam(steamGameId: 20, appId: 222, initial: 12_000, gameId: 700, name: "Floppy Knights (App 222)");
        var row = Row(gameId: 700, steamGameId: 20, price: 5_000);

        var candidate = CatalogDealSelection.ToCandidate(row, ownSnapshot, game, 2_500);
        CheckEqual(12_000, candidate.BasePriceMinor, "base price comes from the row's own steam snapshot");
        CheckEqual("offer", candidate.OfferKey, "a provider row keeps its own offer key");
        CheckEqual("Floppy Knights (App 222)", candidate.GameName, "game name comes from the row's own snapshot");

        // With the row's own base (12,000) a 5,000 price is 58% off: the score cannot reach 7.
        var decision = DealAlertPolicy.Evaluate(candidate, Context);
        CheckEqual(3d, decision.Score, "the shared score of the row's own base is 3.0");
        Check(!In(decision, CatalogDealCategories.Score), "the row does not qualify by score with its own base");

        // The other snapshot of the same game would have produced a false positive (8.5).
        var wrongSnapshotScore = DealAlertPolicy.DealScore(100_000, "MXN", 5_000, 2_500, "MXN", 50);
        CheckEqual(8.5d, wrongSnapshotScore, "the wrong snapshot would have scored 8.5");

        // A mismatched snapshot is refused instead of silently used.
        try
        {
            CatalogDealSelection.ToCandidate(row, firstSnapshot, game, 2_500);
            Failures.Add("a steam snapshot of another app id must be refused");
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>
    /// The provider MXN history low is keyed by the exact <c>steam_game_id</c>, like the notifier. A Steam-less
    /// offer row has no key, so the game's Steam low can never leak into it and fabricate a score.
    /// </summary>
    private static void HistoryLowIsKeyedByTheExactSteamSnapshot()
    {
        var game = GameRow(700);
        var steamLessRow = Row(gameId: 700, steamGameId: null, source: "microsoft", price: 5_000);

        var candidate = CatalogDealSelection.ToCandidate(steamLessRow, steam: null, game, mxnHistoryLowMinor: null);
        Check(candidate.BasePriceMinor is null, "a Steam-less row has no Steam base");
        Check(candidate.MxnHistoryLowMinor is null, "a Steam-less row has no provider MXN low input");
        CheckEqual(null, DealAlertPolicy.DealScore(
            candidate.BasePriceMinor, candidate.BaseCurrency, candidate.MxnCurrentPriceMinor,
            candidate.MxnHistoryLowMinor, candidate.MxnHistoryLowCurrency, 50),
            "no fabricated score without a Steam snapshot");

        // A row that does carry a snapshot gets that snapshot's low, and it is the same number the notifier uses.
        var row = Row(gameId: 700, steamGameId: 20, price: 5_000);
        var withLow = CatalogDealSelection.ToCandidate(row, Steam(steamGameId: 20, gameId: 700), game, 2_500);
        CheckEqual(2_500, withLow.MxnHistoryLowMinor, "the row's own steam snapshot carries the low");
        CheckEqual("MXN", withLow.MxnHistoryLowCurrency, "the low is labelled MXN");
    }

    // --- Parity with the notifier -------------------------------------------

    /// <summary>
    /// The catalog and the notifier must build the same candidate for the same persisted row, because the dedup
    /// key, the score inputs and the card's label are all derived from it. The notifier's private mapping is
    /// invoked through reflection on purpose: a copy of it here would be a second source of truth, and the
    /// production API needs no public seam for a console check.
    /// </summary>
    private static void NotifierCandidateParity()
    {
        var map = typeof(DealAlertCandidateReader).GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static);
        var mapSteam = typeof(DealAlertCandidateReader).GetMethod("MapSteam", BindingFlags.NonPublic | BindingFlags.Static);

        if (map is null || mapSteam is null)
        {
            Failures.Add("notifier mapping methods 'Map'/'MapSteam' were not found (renamed?) — parity cannot be asserted");
            return;
        }

        var game = GameRow(100);
        var steam = Steam(steamGameId: 42, initial: 30_000);
        var offer = Offer(price: 4_199, steamGameId: 42, historyLow: 4_100, historyCurrency: "MXN");
        var providerRow = CatalogDealQueries.ProviderRowProjection().Compile()(offer);
        var catalogProvider = CatalogDealSelection.ToCandidate(providerRow, steam, game, 4_100);
        var notifierProvider = (DealAlertCandidate)map.Invoke(null, [offer, steam, game, 4_100])!;
        AssertCandidateParity(catalogProvider, notifierProvider, "provider row");

        var directSteam = Steam(steamGameId: 43, appId: 2_208_920, current: 3_999, initial: 100_000);
        var directRow = CatalogDealQueries.SteamDirectRowProjection().Compile()(directSteam);
        var catalogDirect = CatalogDealSelection.ToCandidate(directRow, directSteam, game, 2_000);
        var notifierDirect = (DealAlertCandidate)mapSteam.Invoke(null, [directSteam, 2_000])!;
        AssertCandidateParity(catalogDirect, notifierDirect, "steam direct row");

        CheckEqual(directSteam.AppId.ToString(CultureInfo.InvariantCulture), catalogDirect.OfferKey,
            "steam direct offer key is the app id string the notifier uses");
        CheckEqual(notifierDirect.OfferKey, catalogDirect.OfferKey, "steam direct offer key parity");
        CheckEqual(
            $"v{DealAlertPolicy.TriggerVersion}|g100|steam|{directSteam.AppId}|3999",
            DealAlertPolicy.DedupKey(catalogDirect),
            "steam direct dedup key is the notifier's key");

        // The region gate parity that motivated this check: the notifier carries the row's OWN region into the
        // candidate, and the shared policy refuses anything that is not the catalog's lowercase `mx`. A foreign
        // provider row converted under 50 MXN, or a foreign Steam snapshot quoted in MXN under 50, is exactly the
        // case that used to be pushed by the notifier and hidden by the catalog.
        var foreignOffer = Offer(price: 1_999, region: "us", currency: "USD", original: 99);
        var foreignProvider = (DealAlertCandidate)map.Invoke(null, [foreignOffer, steam, game, null])!;
        CheckEqual("us", foreignProvider.Region, "the notifier keeps the provider row's own region");
        Check(!DealAlertPolicy.Evaluate(foreignProvider, Context).Triggered,
            "a foreign provider row never fires, even converted under 50 MXN");

        var foreignSteam = Steam(steamGameId: 44, appId: 2_208_921, current: 3_999, region: "us");
        var foreignDirect = (DealAlertCandidate)mapSteam.Invoke(null, [foreignSteam, null])!;
        CheckEqual("us", foreignDirect.Region, "the notifier keeps the Steam row's own region");
        Check(!DealAlertPolicy.Evaluate(foreignDirect, Context).Triggered,
            "a foreign Steam snapshot never fires, even in MXN under 50");
    }

    private static void AssertCandidateParity(DealAlertCandidate catalog, DealAlertCandidate notifier, string scenario)
    {
        CheckEqual(DealAlertPolicy.DedupKey(notifier), DealAlertPolicy.DedupKey(catalog), $"{scenario}: dedup key");
        CheckEqual(notifier.GameId, catalog.GameId, $"{scenario}: game id");
        CheckEqual(notifier.SteamGameId, catalog.SteamGameId, $"{scenario}: steam game id");
        CheckEqual(notifier.GameName, catalog.GameName, $"{scenario}: game name");
        CheckEqual(notifier.Source, catalog.Source, $"{scenario}: source");
        CheckEqual(notifier.OfferKey, catalog.OfferKey, $"{scenario}: offer key");
        CheckEqual(notifier.ShopName, catalog.ShopName, $"{scenario}: shop");
        CheckEqual(notifier.Classification, catalog.Classification, $"{scenario}: classification");
        CheckEqual(notifier.PricingType, catalog.PricingType, $"{scenario}: pricing type");
        CheckEqual(notifier.Region, catalog.Region, $"{scenario}: region");
        CheckEqual(notifier.OriginalCurrency, catalog.OriginalCurrency, $"{scenario}: original currency");
        CheckEqual(notifier.OriginalCurrentPriceMinor, catalog.OriginalCurrentPriceMinor, $"{scenario}: original price");
        CheckEqual(notifier.MxnCurrentPriceMinor, catalog.MxnCurrentPriceMinor, $"{scenario}: mxn price");
        CheckEqual(notifier.HistoryLowAllMinor, catalog.HistoryLowAllMinor, $"{scenario}: history low");
        CheckEqual(notifier.HistoryLowCurrency, catalog.HistoryLowCurrency, $"{scenario}: history low currency");
        CheckEqual(notifier.ObservedAt, catalog.ObservedAt, $"{scenario}: observed at");
        CheckEqual(notifier.BasePriceMinor, catalog.BasePriceMinor, $"{scenario}: base price");
        CheckEqual(notifier.BaseCurrency, catalog.BaseCurrency, $"{scenario}: base currency");
        CheckEqual(notifier.MxnHistoryLowMinor, catalog.MxnHistoryLowMinor, $"{scenario}: mxn history low");
        CheckEqual(notifier.MxnHistoryLowCurrency, catalog.MxnHistoryLowCurrency, $"{scenario}: mxn history low currency");
        CheckEqual(notifier.DealUrl, catalog.DealUrl, $"{scenario}: deal url");
        CheckEqual(
            DealAlertPolicy.Evaluate(notifier, Context).Score,
            DealAlertPolicy.Evaluate(catalog, Context).Score,
            $"{scenario}: score");
        CheckEqual(
            DealAlertPolicy.Evaluate(notifier, Context).Triggered,
            DealAlertPolicy.Evaluate(catalog, Context).Triggered,
            $"{scenario}: decision");
    }

    // --- Offline EF/Npgsql translation --------------------------------------

    /// <summary>
    /// Renders every catalog query with the production provider. <c>ToQueryString()</c> compiles the query and
    /// prints the SQL; it never opens a connection, so this runs with no database, no network and no secrets.
    /// A query EF cannot translate throws here, which is the only honest way to know before production: this
    /// check is what caught that EF Core 9 refuses a set operation over a positional-constructor projection.
    /// Set <c>CATALOG_SQL_DUMP=1</c> to print the generated SQL for every query.
    /// </summary>
    private static void EfTranslationIsObserved()
    {
        using var db = CreateContext();
        var repository = new Repository(db);
        var offers = repository.Get<GameOffer>();
        var steamGames = repository.Get<SteamGame>();

        var under50 = CatalogDealQueries.Universe(offers, steamGames, Context, CatalogDealCategories.Under50);
        var historic = CatalogDealQueries.Universe(offers, steamGames, Context, CatalogDealCategories.Historic);
        var universe = CatalogDealQueries.Universe(offers, steamGames, Context);
        var mxnSnapshots = CatalogDealQueries.MxnSteamSnapshots(steamGames);

        var under50Sql = Observe(under50, "universe/under50");
        var historicSql = Observe(historic, "universe/historic");
        var universeSql = Observe(universe, "universe/hydration");
        var countSql = Observe(CatalogDealQueries.CategoryGameIds(under50), "count/under50");
        var pageSql = Observe(CatalogDealQueries.CategoryPage(under50, 40, 20), "page/under50");
        var scoreJoinSql = Observe(
            CatalogDealQueries.ScoreProviderCandidates(offers, mxnSnapshots, Context),
            "score/provider-join");
        var scoreDirectSql = Observe(
            CatalogDealQueries.ScoreSteamDirectCandidates(steamGames, Context),
            "score/steam-direct");
        var historyLowsSql = Observe(CatalogDealQueries.HistoryLows(offers, [42, 43]), "history-lows");
        var rowsForGamesSql = Observe(CatalogDealQueries.RowsForGames(universe, [1L, 2L]), "hydration/rows-for-games");

        // The shapes that matter, asserted on the rendered SQL rather than assumed.
        Check(Has(under50Sql, "UNION ALL"), "the under50 universe is one query over provider rows and Steam direct");
        Check(Has(under50Sql, "region = "), "the under50 universe filters region in SQL");
        Check(Has(under50Sql, "observed_at"), "the under50 universe filters freshness in SQL");
        Check(Has(under50Sql, "game_id IS NOT NULL"), "the under50 universe requires a canonical anchor");
        Check(Has(under50Sql, "app_id"), "the Steam direct offer key is rendered from app_id");

        Check(!Has(historicSql, "UNION ALL"), "the historic universe is provider rows only");
        Check(Has(historicSql, "history_low_all_minor"), "the historic universe filters the verified low in SQL");

        Check(Has(countSql, "DISTINCT"), "the tab total is a distinct game count");
        Check(Has(pageSql, "GROUP BY"), "the page groups by game");
        Check(Has(pageSql, "MIN("), "the page takes the cheapest qualifying price");
        Check(Has(pageSql, "LIMIT"), "the page is bounded in SQL");
        Check(Has(pageSql, "OFFSET"), "the page skips in SQL");

        Check(Has(scoreJoinSql, "JOIN"), "the score candidates join the Steam snapshot");
        Check(Has(scoreJoinSql, "steam_game_id"), "the score join is on the exact steam_game_id");
        Check(Has(scoreJoinSql, "bigint") || Has(scoreJoinSql, "CAST"), "the >50% pre-filter widens the product to bigint");
        Check(Has(scoreDirectSql, "steam_games"), "the Steam direct score candidate reads steam_games");
        Check(Has(universeSql, "app_id"), "the Steam direct offer key is rendered from app_id");

        Check(Has(historyLowsSql, "GROUP BY") && Has(historyLowsSql, "steam_game_id"),
            "history lows are grouped by steam_game_id");
        Check(Has(historyLowsSql, "MIN("), "history lows take the minimum");
        Check(Has(rowsForGamesSql, "= ANY"), "hydration filters the page's game ids in SQL");
    }

    private static AppDbContext CreateContext()
    {
        // A dummy connection string is enough: translating and rendering a query never connects.
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
            if (Environment.GetEnvironmentVariable("CATALOG_SQL_DUMP") == "1")
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

        public string GetName() => "catalog-checks";

        public string GetEmail() => "catalog-checks@example.test";

        public bool IsAdmin() => false;
    }
}
