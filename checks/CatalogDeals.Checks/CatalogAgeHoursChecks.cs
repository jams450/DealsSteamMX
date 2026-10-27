#nullable enable
using Deals.API.Services.Jobs;
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
/// Spec S2/S3: the chosen <c>ageHours</c> of the manual pending-price pass must be the SAME window everywhere —
/// the cheapest-price winner's bucket, the per-provider due gates, the selection, the before/after measurement and
/// the remaining count — while the monitor and the wishlist badges stay fixed at 72 h.
///
/// Every case is stated at 24, 72 and 168 hours over the same persisted fixture, so a hidden 72 h constant fails
/// here instead of only in production. The pass runs offline: the real service, the real queries and the real
/// repository over an in-memory database, with the provider doubled, so "no provider traffic" and "which window the
/// phase actually received" are observations rather than promises.
///
/// Failures are collected instead of thrown on the first one: the point of this suite is the seven places the
/// window has to travel through, and one missing place must not hide the state of the rest.
/// </summary>
internal static class CatalogAgeHoursChecks
{
    private const int Requester = 1;
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly List<string> Failures = [];

    public static void Run()
    {
        Section(MonitorStaysFixed);
        Section(ClassificationFollowsChosenHours);
        Section(WinnerEligibilityIgnoresAge);
        Section(DueGatesFollowChosenHours);
        Section(SelectionPartitionsAtTheChosenWindow);
        Section(PassUsesTheChosenWindow);

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: {Failures.Count} catalog age-hours checks failed");
            foreach (var failure in Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            Environment.Exit(1);
        }

        Console.WriteLine("PASS: catalog age-hours checks");
    }

    /// <summary>
    /// The monitor is not configurable: it keeps the documented 72 h window and reports it. The job's default is the
    /// same number, but it is a default of a bounded choice (1..720), not the monitor's constant reused as a ceiling.
    /// </summary>
    private static void MonitorStaysFixed()
    {
        CheckEqual(72, CatalogFreshness.Hours, "the monitor window is still 72 h");
        CheckEqual(Now.AddHours(-72), CatalogFreshness.Cutoff(Now), "the monitor cutoff is still 72 h");
        CheckEqual(Now.AddHours(-168), CatalogFreshness.Cutoff(Now, 168), "a chosen cutoff is the chosen hours");

        CheckEqual(1, CatalogPriceRefreshLimits.MinAgeHours, "the smallest chosen window is 1 h");
        CheckEqual(720, CatalogPriceRefreshLimits.MaxAgeHours, "the largest chosen window is 720 h");
        CheckEqual(72, CatalogPriceRefreshLimits.DefaultAgeHours, "the default chosen window is 72 h");

        foreach (var invalid in new[] { 0, -1, 721, int.MaxValue })
        {
            var rejected = false;
            try
            {
                CatalogPriceRefreshLimits.ValidateAgeHours(invalid);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }

            Check(rejected, $"a chosen window of {invalid} h is refused by the pass itself");
        }

        foreach (var valid in new[] { 1, 72, 720 })
        {
            CheckEqual(valid, CatalogPriceRefreshLimits.ValidateAgeHours(valid),
                $"{valid} h is a valid chosen window");
        }
    }

    /// <summary>
    /// The bucket of a game follows the CHOSEN price's age at the requested window: a 30-hour-old price is stale for
    /// 24 h, recent for 72 h and 168 h; a 100-hour-old price is stale for 72 h and recent only for 168 h.
    /// </summary>
    private static void ClassificationFollowsChosenHours()
    {
        var games = new[] { Game(11), Game(13), Game(14) }.AsQueryable();
        var prices = new[]
        {
            Price(11, 500, Now.AddHours(-30)),
            Price(13, 500, Now.AddHours(-100)),
            // Game 14 has no usable price at all: missing at every window.
        }.AsQueryable();

        var at24 = ByGame(CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: 24));
        var at72 = ByGame(CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: 72));
        var at168 = ByGame(CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: 168));

        CheckEqual(CatalogFreshnessStatus.Stale, at24[11].Status, "a 30 h old price is stale at 24 h");
        CheckEqual(CatalogFreshnessStatus.Recent, at72[11].Status, "the same price is recent at 72 h");
        CheckEqual(CatalogFreshnessStatus.Recent, at168[11].Status, "the same price is recent at 168 h");

        CheckEqual(CatalogFreshnessStatus.Stale, at24[13].Status, "a 100 h old price is stale at 24 h");
        CheckEqual(CatalogFreshnessStatus.Stale, at72[13].Status, "a 100 h old price is stale at 72 h");
        CheckEqual(CatalogFreshnessStatus.Recent, at168[13].Status, "a 100 h old price is recent at 168 h");

        foreach (var (hours, states) in new[] { (24, at24), (72, at72), (168, at168) })
        {
            CheckEqual(CatalogFreshnessStatus.Missing, states[14].Status,
                $"a game with no price stays missing at {hours} h");
            CheckEqual(null, states[14].ChosenObservedAt,
                $"a game with no price has no chosen instant at {hours} h");
        }

        // PRESERVE: the monitor's own call (no age argument) still means 72 h.
        var monitor = ByGame(CatalogFreshnessQueries.GameStates(games, prices, Now));
        CheckEqual(at72[11].Status, monitor[11].Status, "the monitor's default window is unchanged");
        CheckEqual(at72[13].Status, monitor[13].Status, "the monitor's default window is unchanged");
    }

    /// <summary>
    /// The winner is the cheapest stored price regardless of the window: a fresh, more expensive Steam price never
    /// displaces an older cheaper external offer. Only the BUCKET of that winner changes with the chosen hours, so
    /// price and label keep coming from one row.
    /// </summary>
    private static void WinnerEligibilityIgnoresAge()
    {
        var games = new[] { Game(10) }.AsQueryable();
        var prices = new[]
        {
            Price(10, 200, Now.AddHours(-100), provider: "itad", offerKey: "itad", shop: "ITAD"),
            Price(10, 500, Now.AddHours(-10), provider: "steam", offerKey: "110", shop: "Steam"),
        }.AsQueryable();

        foreach (var hours in new[] { 24, 72, 168 })
        {
            var state = CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: hours).Single();
            CheckEqual(200, state.ChosenPriceMinor, $"the cheapest price wins at {hours} h");
            CheckEqual("itad", state.ChosenProvider, $"the winner's provider travels with the amount at {hours} h");
            CheckEqual("ITAD", state.ChosenShopName, $"the winner's shop travels with the amount at {hours} h");
            CheckEqual(
                Now.AddHours(-100),
                state.ChosenObservedAt,
                $"the winner's instant is the cheap row's, not the fresh expensive one at {hours} h");
        }

        var at24 = CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: 24).Single();
        var at168 = CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: 168).Single();
        CheckEqual(CatalogFreshnessStatus.Stale, at24.Status, "the cheap winner is stale at 24 h");
        CheckEqual(CatalogFreshnessStatus.Recent, at168.Status, "the same winner is recent at 168 h");
    }

    /// <summary>
    /// A provider phase is due when its stamp is older than the CHOSEN window. The same 100 h old stamp is due at
    /// 24 h and 72 h and no longer due at 168 h, and the complement used by the not-due counter agrees with it.
    /// </summary>
    private static void DueGatesFollowChosenHours()
    {
        var old = Snapshot(13, 1300, Now.AddHours(-100));
        var fresh = Snapshot(14, 1400, Now.AddHours(-1));

        foreach (var snapshot in new[] { old, fresh })
        {
            var due = CatalogFreshnessQueries.DuePhaseGate(Now, 24).Compile()(snapshot);
            var notDue = CatalogFreshnessQueries.NotDuePhaseGate(Now, 24).Compile()(snapshot);
            Check(due != notDue, "the due gate and its complement stay exclusive");
        }

        Check(CatalogFreshnessQueries.DuePhaseGate(Now, 24).Compile()(old), "a 100 h stamp is due at 24 h");
        Check(CatalogFreshnessQueries.DuePhaseGate(Now, 72).Compile()(old), "a 100 h stamp is due at 72 h");
        Check(!CatalogFreshnessQueries.DuePhaseGate(Now, 168).Compile()(old), "a 100 h stamp is not due at 168 h");

        Check(!CatalogFreshnessQueries.DuePhaseGate(Now, 24).Compile()(fresh), "a 1 h stamp is not due at 24 h");
        Check(!CatalogFreshnessQueries.DuePhaseGate(Now, 720).Compile()(fresh), "a 1 h stamp is not due at 720 h");

        // PRESERVE: the gate without an age argument is still the 72 h monitor window.
        CheckEqual(
            CatalogFreshnessQueries.DuePhaseGate(Now, 72).Compile()(old),
            CatalogFreshnessQueries.DuePhaseGate(Now).Compile()(old),
            "the default due gate is unchanged");
    }

    /// <summary>
    /// Selection, its not-due complement and the batch bound all read the chosen window. A game whose chosen price is
    /// recent for a wide window is not pending at all, so it cannot be selected; a game that is pending but whose
    /// phases are fresh counts as not due, and the two sets partition the pending set at every window.
    /// </summary>
    private static void SelectionPartitionsAtTheChosenWindow()
    {
        var games = new[] { Game(11), Game(13) }.AsQueryable();
        var prices = new[]
        {
            Price(11, 500, Now.AddHours(-30)),
            Price(13, 500, Now.AddHours(-100)),
        }.AsQueryable();
        var snapshots = new[]
        {
            Steam(11, 1100, Now.AddHours(-100), steamGameId: 2101, priceMinor: 9_999),
            Steam(13, 1300, Now.AddHours(-100), steamGameId: 2103, priceMinor: 9_999),
        }.AsQueryable();

        var at24 = CatalogFreshnessQueries.Pending(
            CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: 24));
        var at168 = CatalogFreshnessQueries.Pending(
            CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: 168));

        CheckEqual(2, at24.Count(), "both games are pending at 24 h");
        CheckEqual(0, at168.Count(), "no game is pending at 168 h");

        var selected24 = CatalogFreshnessQueries
            .PendingCandidates(at24, snapshots, CatalogPriceRefreshLimits.BatchSize, Now, ageHours: 24)
            .ToList();
        var selected168 = CatalogFreshnessQueries
            .PendingCandidates(at168, snapshots, CatalogPriceRefreshLimits.BatchSize, Now, ageHours: 168)
            .ToList();

        // Missing first, then oldest chosen price first: 13 (-100 h) before 11 (-30 h).
        CheckEqual("13,11", string.Join(",", selected24.Select(candidate => candidate.GameId)),
            "the chosen window selects both due pending games at 24 h, oldest price first");
        CheckEqual(0, selected168.Count, "a wide window selects nothing because nothing is pending");

        foreach (var hours in new[] { 24, 72, 168 })
        {
            var pending = CatalogFreshnessQueries.Pending(
                CatalogFreshnessQueries.GameStates(games, prices, Now, ageHours: hours));
            var due = CatalogFreshnessQueries
                .PendingCandidates(pending, snapshots, CatalogPriceRefreshLimits.BatchSize, Now, hours)
                .Select(candidate => candidate.GameId)
                .ToList();
            var notDue = CatalogFreshnessQueries
                .PendingNotDue(pending, snapshots, Now, hours)
                .Select(state => state.GameId)
                .ToList();
            var pendingIds = pending.Select(state => state.GameId).ToList();

            CheckEqual(pendingIds.Count, due.Count + notDue.Count,
                $"due and not-due partition the pending set at {hours} h");
            Check(!due.Intersect(notDue).Any(), $"no game is both due and not due at {hours} h");
        }
    }

    /// <summary>
    /// End to end, offline: the same wishlist, priced three times with three windows. The pending split
    /// (<c>olderPricePending</c>/<c>missingPricePending</c>), the selection, the window each provider phase received
    /// and the remaining count all move with the chosen hours; at 168 h nothing is pending and no provider is called.
    /// </summary>
    private static void PassUsesTheChosenWindow()
    {
        // 30 h old price, 100 h old price, and a game with no price at all. All provider stamps are as old as the
        // prices, so at each window the due gate agrees with the bucket.
        using var at24 = Seed(Now.AddHours(-30), Now.AddHours(-100));
        var provider24 = new RecordingSteamGameService();
        var outcome24 = Service(at24, provider24).RefreshPendingAsync(Requester, CancellationToken.None, 24)
            .GetAwaiter().GetResult();

        CheckEqual(24, outcome24.AgeHours, "the outcome reports the window it used");
        CheckEqual(3, outcome24.Pending, "all three games are pending at 24 h");
        CheckEqual(2, outcome24.OlderPricePending, "two games have an old price at 24 h");
        CheckEqual(1, outcome24.MissingPricePending, "one game has no price at 24 h");
        CheckEqual(3, outcome24.Selected, "all three are selected at 24 h");
        CheckEqual("1100,1200,1300", string.Join(",", provider24.Calls.OrderBy(appId => appId)),
            "every pending game is asked at 24 h");
        CheckEqual("24,24,24", string.Join(",", provider24.AgeHours),
            "every provider phase receives the chosen window");
        CheckEqual(3, outcome24.Remaining,
            "the remaining count is measured with the same window (all three are still pending)");

        using var at72 = Seed(Now.AddHours(-30), Now.AddHours(-100));
        var provider72 = new RecordingSteamGameService();
        var outcome72 = Service(at72, provider72).RefreshPendingAsync(Requester, CancellationToken.None, 72)
            .GetAwaiter().GetResult();

        CheckEqual(72, outcome72.AgeHours, "the default-shaped window is reported too");
        CheckEqual(2, outcome72.Pending, "the 30 h old price is no longer pending at 72 h");
        CheckEqual(1, outcome72.OlderPricePending, "only the 100 h old price is pending at 72 h");
        CheckEqual(1, outcome72.MissingPricePending, "the missing game is still pending at 72 h");
        CheckEqual(2, outcome72.Selected, "only the pending games are selected at 72 h");
        CheckEqual("1200,1300", string.Join(",", provider72.Calls.OrderBy(appId => appId)),
            "a game that is not pending is never asked");
        CheckEqual("72,72", string.Join(",", provider72.AgeHours), "the phase receives 72 h when 72 h was chosen");

        using var at168 = Seed(Now.AddHours(-30), Now.AddHours(-100));
        var provider168 = new RecordingSteamGameService();
        var outcome168 = Service(at168, provider168).RefreshPendingAsync(Requester, CancellationToken.None, 168)
            .GetAwaiter().GetResult();

        CheckEqual(168, outcome168.AgeHours, "the wide window is reported");
        CheckEqual(1, outcome168.Pending, "only the game without a price is pending at 168 h");
        CheckEqual(0, outcome168.OlderPricePending, "no old price is pending at 168 h");
        CheckEqual(1, outcome168.MissingPricePending, "a missing price is pending at every window");
        CheckEqual(0, outcome168.Selected, "nothing is due at 168 h");
        CheckEqual(0, provider168.Calls.Count, "no provider traffic when nothing is due");
        CheckEqual(1, outcome168.Remaining,
            "the remaining count uses 168 h too: the 100 h old price is no longer pending, the missing one is");

        // The default of the pass is the documented 72 h, not a hidden constant of its own.
        using var byDefault = Seed(Now.AddHours(-30), Now.AddHours(-100));
        var outcomeDefault = Service(byDefault, new RecordingSteamGameService())
            .RefreshPendingAsync(Requester, CancellationToken.None)
            .GetAwaiter().GetResult();
        CheckEqual(CatalogPriceRefreshLimits.DefaultAgeHours, outcomeDefault.AgeHours,
            "an omitted window is the documented default");
        CheckEqual(2, outcomeDefault.Pending, "the default window behaves exactly like 72 h");

        // Spec S2: the pass itself refuses a window outside 1..720 before touching a provider.
        using var refused = Seed(Now.AddHours(-30), Now.AddHours(-100));
        var refusedProvider = new RecordingSteamGameService();
        foreach (var invalid in new[] { 0, 721 })
        {
            var threw = false;
            try
            {
                Service(refused, refusedProvider)
                    .RefreshPendingAsync(Requester, CancellationToken.None, invalid)
                    .GetAwaiter().GetResult();
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Check(threw, $"the pass refuses a window of {invalid} h");
        }

        CheckEqual(0, refusedProvider.Calls.Count, "a refused window makes no provider call");

        // The job record carries the window and the two pending counts explicitly.
        using var recorded = Seed(Now.AddHours(-30), Now.AddHours(-100));
        var details = Details(
            new CatalogPriceRefreshJobExecutor(Service(recorded, new RecordingSteamGameService()))
                .ExecuteAsync(
                    new JobRunOptions(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [JobOptionKeys.AgeHours] = 24
                    }),
                    Requester,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult());

        CheckEqual(24, details.GetProperty("ageHours").GetInt32(), "the job record shows the chosen hours");
        CheckEqual(2, details.GetProperty("olderPricePending").GetInt32(),
            "the job record separates the old-price pending games");
        CheckEqual(1, details.GetProperty("missingPricePending").GetInt32(),
            "the job record separates the missing-price pending games");
        CheckEqual(3, details.GetProperty("pending").GetInt32(), "the total pending count is unchanged");
    }

    // --- Fixture ----------------------------------------------------------

    /// <summary>
    /// One requester's wishlist: a game priced 30 h ago, one priced 100 h ago and one with no price at all. The Steam
    /// snapshot of each carries provider stamps as old as the price it holds, so the bucket and the due gate agree.
    /// </summary>
    private static AppDbContext Seed(DateTime firstPrice, DateTime secondPrice)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"catalog-age-hours-{Guid.NewGuid():N}")
            .Options;

        var db = new AppDbContext(options, new OfflineUser());
        db.Games.AddRange(GameRow(11, "Precio de 30 h"), GameRow(12, "Sin precio"), GameRow(13, "Precio de 100 h"));
        db.GameOffers.AddRange(Offer(11, 500, firstPrice), Offer(13, 700, secondPrice));
        // Game 12 has a snapshot with no usable amount on purpose: that is what "missing price" means here.
        db.SteamGames.AddRange(
            Steam(11, 1100, firstPrice, steamGameId: 2101, priceMinor: 9_999),
            Steam(12, 1200, secondPrice, steamGameId: 2102, priceMinor: null),
            Steam(13, 1300, secondPrice, steamGameId: 2103, priceMinor: 9_999));

        db.UserLibrary.AddRange(
            Wished(1, Requester, "steam", "1100", gameId: 11),
            Wished(2, Requester, "steam", "1200", gameId: 12),
            Wished(3, Requester, "steam", "1300", gameId: 13));

        db.SaveChanges();
        return db;
    }

    private static CatalogPriceRefreshService Service(AppDbContext db, ISteamGameService provider) =>
        new(
            new Repository(db),
            provider,
            new FixedClock(Now),
            NullLogger<CatalogPriceRefreshService>.Instance,
            new CatalogWishlistScopeResolver(new Repository(db)));

    private static Game GameRow(long gameId, string title) =>
        new() { GameId = gameId, Title = title, NormalizedTitle = title };

    private static Game Game(long gameId) => new() { GameId = gameId, Title = $"Juego {gameId}" };

    private static GameOffer Offer(long gameId, int priceMinor, DateTime observedAt) =>
        new()
        {
            GameOfferId = gameId * 10,
            GameId = gameId,
            SteamGameId = null,
            Region = "mx",
            Source = "itad",
            OfferKey = "itad",
            ShopName = "ITAD",
            Classification = "official",
            OriginalCurrency = "MXN",
            OriginalRegularPriceMinor = priceMinor,
            OriginalCurrentPriceMinor = priceMinor,
            MxnRegularPriceMinor = priceMinor,
            MxnCurrentPriceMinor = priceMinor,
            PricingType = "regional",
            ObservedAt = observedAt,
        };

    private static SteamGame Steam(
        long gameId,
        int appId,
        DateTime observedAt,
        int steamGameId,
        int? priceMinor) =>
        new()
        {
            SteamGameId = steamGameId,
            AppId = appId,
            GameId = gameId,
            Region = "mx",
            Name = $"Juego {gameId}",
            Currency = "MXN",
            InitialPriceMinor = 9_999,
            CurrentPriceMinor = priceMinor,
            ObservedAt = observedAt,
            OffersRefreshedAt = observedAt,
            GgDealsRefreshedAt = observedAt,
            EpicRefreshedAt = observedAt,
            MicrosoftRefreshedAt = observedAt,
        };

    private static UserLibrary Wished(long userLibraryId, int userId, string store, string storeGameId, long? gameId) =>
        new()
        {
            UserLibraryId = userLibraryId,
            UserId = userId,
            GameId = gameId,
            Store = store,
            StoreGameId = storeGameId,
            Title = $"{store}:{storeGameId}",
            State = LibraryStates.Wished,
            ImportedAt = Now.AddDays(-30),
        };

    private static CatalogFreshnessPriceRow Price(
        long gameId,
        int priceMinor,
        DateTime observedAt,
        string provider = "itad",
        string offerKey = "itad",
        string shop = "ITAD") =>
        new()
        {
            GameId = gameId,
            Provider = provider,
            OfferKey = offerKey,
            StableKey = provider + "\u0001" + offerKey,
            ShopName = shop,
            PricingType = "regional",
            PriceMinor = priceMinor,
            ObservedAt = observedAt,
        };

    private static CatalogFreshnessSteamRow Snapshot(long gameId, int appId, DateTime stamp) =>
        new()
        {
            GameId = gameId,
            SteamGameId = appId,
            AppId = appId,
            ObservedAt = stamp,
            OffersRefreshedAt = stamp,
            GgDealsRefreshedAt = stamp,
            EpicRefreshedAt = stamp,
            MicrosoftRefreshedAt = stamp,
        };

    private static Dictionary<long, CatalogFreshnessGameState> ByGame(IQueryable<CatalogFreshnessGameState> states) =>
        states.ToList().ToDictionary(state => state.GameId);

    private static System.Text.Json.JsonElement Details(JobExecutionResult result)
    {
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            result.Details,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return document.RootElement.Clone();
    }

    /// <summary>Fixed clock: every timestamp of the fixture is expressed relative to one instant.</summary>
    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    /// <summary>
    /// Provider double: records the app id AND the window each phase was asked for, and answers a declined attempt so
    /// the pass is exercised without inventing a price. Every other member throws — the pass must not ask for
    /// details, search or discovery.
    /// </summary>
    private sealed class RecordingSteamGameService : ISteamGameService
    {
        public List<int> Calls { get; } = [];

        public List<int> AgeHours { get; } = [];

        public Task<SteamStalePriceRefresh> RefreshStalePricesAsync(
            int appId,
            CancellationToken cancellationToken,
            int ageHours = GameRefreshPolicy.StalePriceBatchHours)
        {
            Calls.Add(appId);
            AgeHours.Add(ageHours);
            return Task.FromResult(SteamStalePriceRefresh.Declined());
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
            throw new NotSupportedException("the price pass uses its own batch entry point");

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

    private sealed class OfflineUser : ICurrentUserService
    {
        public int? GetUserId() => null;

        public int GetRequiredUserId() => throw new InvalidOperationException("age-hour checks have no request user");

        public int? GetSessionVersion() => null;

        public Guid? GetSessionId() => null;

        public string GetName() => "age-hours-checks";

        public string GetEmail() => "age-hours-checks@example.test";

        public bool IsAdmin() => false;
    }

    // --- Helpers ----------------------------------------------------------

    private static void Section(Action section)
    {
        try
        {
            section();
        }
        catch (Exception exception)
        {
            Failures.Add($"{section.Method.Name}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Check(bool value, string name)
    {
        if (!value) Failures.Add(name);
    }

    private static void CheckEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Failures.Add($"{name}: expected {expected}, got {actual}");
    }
}
