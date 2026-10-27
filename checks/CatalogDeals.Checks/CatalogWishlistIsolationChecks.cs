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
/// Isolation checks for the wishlist-scoped price monitor and the wishlist-scoped manual price pass
/// (spec S4/S5, user correction: «esto no es lo que quiero es solo whislist» / «Sí, panel y job solo de mi
/// wishlist»).
///
/// Everything runs offline against an in-memory <see cref="AppDbContext"/>: the real
/// <see cref="Repository"/>, the real <see cref="CatalogWishlistScopeResolver"/> and the real services run,
/// and only the provider client is doubled — so "no provider call" is an observation, not a promise.
///
/// The fixture is deliberately built from the four ways a game can reach a user and only one of them may be
/// read:
/// <list type="bullet">
/// <item>a wished row of the requester (canonical id present) — included;</item>
/// <item>a wished row of the requester with no canonical id but an exact Steam app id — included through the
/// <c>steam_games</c> mapping, never through the title;</item>
/// <item>an <c>owned</c> row of the requester (the library) and a favourite that is not wished — excluded;</item>
/// <item>another user's wished row — excluded.</item>
/// </list>
/// A wished row with no canonical id and no Steam identity (the <c>epic</c> row below) is excluded too: this
/// pass has no exact way to attribute it, and guessing would monitor somebody else's game.
/// </summary>
internal static class CatalogWishlistIsolationChecks
{
    private const int Requester = 1;
    private const int OtherUser = 2;
    private const int EmptyUser = 3;

    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly List<string> Failures = [];

    public static void Run()
    {
        ScopeIsOnlyTheRequestersWishlist();
        MonitorIsScopedToTheWishlist();
        EmptyWishlistMonitorsNothing();
        JobIsScopedToTheWishlist();
        MissingRequesterNeverReachesThePass();
        UnresolvedEntriesAreCounted();
        ResolveBoundaryCases();
        CoveredWishlistWithUnresolvedEntries();
        AllUnresolvedWishlistIsReported();
        EfTranslationIsObserved();

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: {Failures.Count} catalog wishlist isolation checks failed");
            foreach (var failure in Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            Environment.Exit(1);
        }

        Console.WriteLine("PASS: catalog wishlist isolation checks");
    }

    /// <summary>
    /// The scope itself: exactly the canonical games of the requester's <c>wished</c> rows, plus the ones an
    /// exact Steam app id maps to. Library rows, favourites and other accounts contribute nothing, and there is
    /// no default account to fall back to.
    /// </summary>
    private static void ScopeIsOnlyTheRequestersWishlist()
    {
        using var db = CreateContext();
        var resolver = new CatalogWishlistScopeResolver(new Repository(db));

        var requester = resolver.LoadAsync(Requester, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual("10,12,13", string.Join(",", requester.GameIds), "the scope is the requester's wished canonical games");
        CheckEqual(Requester, requester.UserId, "the scope remembers whose list it is");
        Check(!requester.GameIds.Contains(11), "an owned game (library) is not part of the wishlist scope");
        Check(!requester.GameIds.Contains(30), "a favourite that is not wished is not part of the wishlist scope");
        Check(!requester.GameIds.Contains(20), "another user's wished game is never part of the scope");
        Check(requester.GameIds.Contains(12), "a wished row with only an exact Steam app id is mapped to its game");
        Check(requester.GameIds.Count == 3, "a wished row with no exact identity adds nothing to the scope");
        CheckEqual(1, requester.UnresolvedEntries,
            "the wished row with no exact identity is counted, not silently dropped");

        var other = resolver.LoadAsync(OtherUser, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual("20", string.Join(",", other.GameIds), "the other user's scope is their own list");
        Check(!other.GameIds.Contains(10), "the requester's games never leak into another user's scope");
        CheckEqual(0, other.UnresolvedEntries, "another account's countable rows are its own");

        var empty = resolver.LoadAsync(EmptyUser, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual(0, empty.GameIds.Count, "a user with no wishlist has an empty scope");
        CheckEqual(0, empty.UnresolvedEntries, "an empty wishlist has nothing unresolved");

        // Fail closed: there is no "account 1" and no configured operator to borrow.
        var refused = false;
        try
        {
            resolver.LoadAsync(0, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (ArgumentException)
        {
            refused = true;
        }

        Check(refused, "an absent user id is refused instead of defaulting to an account");
    }

    /// <summary>
    /// The monitor's counters, page and totals describe the requester's wishlist only. Stale/owned/favourite
    /// games of the same user and another account's games are not counted, listed or paged.
    /// </summary>
    private static void MonitorIsScopedToTheWishlist()
    {
        using var db = CreateContext();
        var service = new CatalogFreshnessService(
            new Repository(db),
            new CatalogWishlistScopeResolver(new Repository(db)));

        var page = service.ListAsync(Requester, "all", 1, 20, Now, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        CheckEqual(3, page.Counts.Total, "the monitor counts only the requester's wishlist games");
        CheckEqual(1, page.Counts.Recent, "one wished game has a fresh comparable price");
        CheckEqual(1, page.Counts.Stale, "one wished game has an old comparable price");
        CheckEqual(1, page.Counts.Missing, "one wished game has no price at all, and is still listed");
        CheckEqual(3, page.Total, "total is the selected filter's total, over the wishlist only");
        CheckEqual("13,12,10", string.Join(",", page.Items.Select(item => item.GameId)),
            "missing first, then the oldest price, and never a non-wished game");
        Check(!page.Items.Any(item => item.GameId is 11 or 20 or 30),
            "neither the library, nor a favourite, nor another account appears in the monitor");
        CheckEqual("Steam-less deseado", page.Items.Single(item => item.GameId == 13).Name,
            "a Steam-less wished game stays visible instead of being dropped");
        CheckEqual(1, page.UnresolvedWishlistEntries,
            "the wished row with no exact identity is reported next to the canonical counters");

        var stale = service.ListAsync(Requester, "stale", 1, 20, Now, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        CheckEqual(1, stale.Total, "the filter is applied to the wishlist, not to the whole catalog");
        CheckEqual("12", string.Join(",", stale.Items.Select(item => item.GameId)),
            "only the requester's stale wished game is listed");
        CheckEqual(3, stale.Counts.Total, "the counters are not narrowed by the selected filter");

        var other = service.ListAsync(OtherUser, "all", 1, 20, Now, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        CheckEqual(1, other.Counts.Total, "another account sees only its own list");
        CheckEqual("20", string.Join(",", other.Items.Select(item => item.GameId)),
            "another account's row is their own game");
    }

    private static void EmptyWishlistMonitorsNothing()
    {
        using var db = CreateContext();
        var service = new CatalogFreshnessService(
            new Repository(db),
            new CatalogWishlistScopeResolver(new Repository(db)));

        var page = service.ListAsync(EmptyUser, "all", 1, 20, Now, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        CheckEqual(0, page.Counts.Total, "an empty wishlist counts no game");
        CheckEqual(0, page.Counts.Recent, "an empty wishlist has no recent game");
        CheckEqual(0, page.Counts.Stale, "an empty wishlist has no stale game");
        CheckEqual(0, page.Counts.Missing, "an empty wishlist has no missing game");
        CheckEqual(0, page.Total, "an empty wishlist has nothing to page");
        CheckEqual(0, page.Items.Count, "an empty wishlist lists no game");
        CheckEqual(0, page.UnresolvedWishlistEntries, "an empty wishlist has no unresolved entry");
        CheckEqual(CatalogFreshness.Hours, page.FreshnessHours, "the window is still reported for the caller");
    }

    /// <summary>
    /// The manual pass is scoped the same way: it selects only the requester's pending wished games, counts the
    /// Steam-less one as unsupported instead of guessing an identity, and never calls a provider for a game
    /// outside that list.
    /// </summary>
    private static void JobIsScopedToTheWishlist()
    {
        using var db = CreateContext();
        var provider = new RecordingSteamGameService();
        var service = new CatalogPriceRefreshService(
            new Repository(db),
            provider,
            new FixedClock(Now),
            NullLogger<CatalogPriceRefreshService>.Instance,
            new CatalogWishlistScopeResolver(new Repository(db)));

        var outcome = service.RefreshPendingAsync(Requester, CancellationToken.None).GetAwaiter().GetResult();

        CheckEqual(1, provider.Calls.Count, "only the requester's priceable wished game is priced");
        CheckEqual("1200", string.Join(",", provider.Calls), "the exact Steam app id of the wished game is used");
        CheckEqual(2, outcome.Pending, "pending is the requester's stale plus missing wished games");
        CheckEqual(1, outcome.Selected, "one wished game can be refreshed");
        CheckEqual(1, outcome.Skipped, "an answer that changed nothing is a skip, not an update");
        CheckEqual(1, outcome.Unsupported, "the Steam-less wished game is counted as unsupported, never invented");
        Check(!provider.Calls.Contains(111), "an owned game is never priced by this pass");
        Check(!provider.Calls.Contains(200), "another account's game is never priced by this pass");
        Check(!provider.Calls.Contains(300), "a favourite that is not wished is never priced by this pass");

        // The other account's pass sees only its own game: there is no shared, catalog-wide batch any more.
        var otherProvider = new RecordingSteamGameService();
        var otherService = new CatalogPriceRefreshService(
            new Repository(db),
            otherProvider,
            new FixedClock(Now),
            NullLogger<CatalogPriceRefreshService>.Instance,
            new CatalogWishlistScopeResolver(new Repository(db)));

        var other = otherService.RefreshPendingAsync(OtherUser, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual("200", string.Join(",", otherProvider.Calls), "another account prices only its own wished game");
        CheckEqual(1, other.Pending, "another account's pending set is its own");
    }

    /// <summary>
    /// A pass asked for without a trusted requester refuses before reading anything: no provider call, no
    /// counters invented, and no fallback to a configured owner.
    /// </summary>
    private static void MissingRequesterNeverReachesThePass()
    {
        using var db = CreateContext();
        var provider = new RecordingSteamGameService();
        var service = new CatalogPriceRefreshService(
            new Repository(db),
            provider,
            new FixedClock(Now),
            NullLogger<CatalogPriceRefreshService>.Instance,
            new CatalogWishlistScopeResolver(new Repository(db)));

        var refused = false;
        try
        {
            service.RefreshPendingAsync(0, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (ArgumentException)
        {
            refused = true;
        }

        Check(refused, "the pass refuses an absent requester");
        CheckEqual(0, provider.Calls.Count, "a refused pass makes no provider call");

        // An empty wishlist is a legitimate answer: nothing pending, and still no provider call.
        var empty = service.RefreshPendingAsync(EmptyUser, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual(0, empty.Pending, "an empty wishlist has nothing pending");
        CheckEqual(0, empty.Selected, "an empty wishlist selects nothing");
        CheckEqual(0, empty.Remaining, "an empty wishlist leaves nothing remaining");
        CheckEqual(0, provider.Calls.Count, "an empty wishlist makes no provider call");
    }

    /// <summary>
    /// Rows the pass cannot attribute are counted as <em>entries</em>, separately from the canonical game
    /// counters, and every way of failing to attribute one is covered: an orphan canonical id, a non-Steam row
    /// with no canonical id, a Steam row whose store id is not an app id, and a Steam row whose app id has no
    /// snapshot. A row whose canonical id is orphaned still resolves through its exact Steam id, and a row whose
    /// canonical id is valid is never re-mapped through Steam.
    /// </summary>
    private static void UnresolvedEntriesAreCounted()
    {
        using var db = CreateAdversarialContext();
        var resolver = new CatalogWishlistScopeResolver(new Repository(db));

        var requester = resolver.LoadAsync(Requester, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual("10,12", string.Join(",", requester.GameIds),
            "only rows with a real canonical identity enter the scope");
        CheckEqual(4, requester.UnresolvedEntries,
            "orphan canonical, no identity, bad app id and unknown app id are each counted once");
        Check(!requester.GameIds.Contains(999) && !requester.GameIds.Contains(888),
            "an orphan canonical id is never adopted as a game");
        CheckEqual(1, requester.GameIds.Count(id => id == 12),
            "two entries that resolve to the same game are one game");
        CheckEqual(1, requester.GameIds.Count(id => id == 10),
            "a valid canonical id wins over any Steam mapping");

        var other = resolver.LoadAsync(OtherUser, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual(0, other.GameIds.Count, "another account's orphan row resolves to no game");
        CheckEqual(1, other.UnresolvedEntries, "another account's unresolved entry is counted for it alone");

        var service = new CatalogFreshnessService(
            new Repository(db),
            new CatalogWishlistScopeResolver(new Repository(db)));
        var page = service.ListAsync(Requester, "all", 1, 20, Now, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        CheckEqual(2, page.Counts.Total, "the canonical counters ignore unresolved entries");
        CheckEqual(1, page.Counts.Recent, "one resolved game is recent");
        CheckEqual(1, page.Counts.Stale, "one resolved game is stale");
        CheckEqual(0, page.Counts.Missing, "no resolved game is missing");
        CheckEqual(4, page.UnresolvedWishlistEntries, "the monitor reports the unresolved entries");
        CheckEqual("12,10", string.Join(",", page.Items.Select(item => item.GameId)),
            "only resolved games are listed, and no game is invented for an unresolved entry");

        var provider = new RecordingSteamGameService();
        var pass = new CatalogPriceRefreshService(
            new Repository(db),
            provider,
            new FixedClock(Now),
            NullLogger<CatalogPriceRefreshService>.Instance,
            new CatalogWishlistScopeResolver(new Repository(db)));

        var outcome = pass.RefreshPendingAsync(Requester, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual("1200", string.Join(",", provider.Calls),
            "the pass prices only the resolved pending game, never an unresolved entry");
        CheckEqual(1, outcome.Selected, "one resolved game is selected");
        CheckEqual(4, outcome.UnresolvedWishlistEntries,
            "the pass reports the same unresolved count as the monitor");

        var details = Details(new CatalogPriceRefreshJobExecutor(pass)
            .ExecuteAsync(JobRunOptions.Empty, Requester, CancellationToken.None)
            .GetAwaiter()
            .GetResult());
        CheckEqual(4, details.GetProperty("unresolvedWishlistEntries").GetInt32(),
            "the job record carries the unresolved count");
        CheckEqual(System.Text.Json.JsonValueKind.Null, details.GetProperty("reason").ValueKind,
            "a run that really priced something needs no excuse");
    }

    /// <summary>
    /// The attribution decision on its own, over rows no database is needed for: valid canonical ids win, a
    /// non-positive id is not an identity, a zero or non-numeric store id is not an app id, the store key is
    /// ordinal (case matters), one app id may map to several games after a merge, and repeated unattributable
    /// rows are counted as separate entries.
    /// </summary>
    private static void ResolveBoundaryCases()
    {
        var rows = new List<CatalogWishlistRow>
        {
            // A valid canonical id is never re-attributed through Steam, even when the store id says otherwise.
            new() { GameId = 10, Store = "steam", StoreGameId = "1200" },
            // Id 0 is not an identity; the exact app id rescues the row.
            new() { GameId = 0, Store = "steam", StoreGameId = "1200" },
            // Negative id and no exact path at all.
            new() { GameId = -5, Store = "epic", StoreGameId = "x" },
            // "0" is not an app id.
            new() { GameId = null, Store = "steam", StoreGameId = "0" },
            // A merged app id maps to two existing games: both enter the scope, once each.
            new() { GameId = null, Store = "steam", StoreGameId = "42" },
            // The same app id twice is one game for the counters.
            new() { GameId = null, Store = "steam", StoreGameId = "1200" },
            // The store key is compared ordinally: "Steam" is not the steam store.
            new() { GameId = null, Store = "Steam", StoreGameId = "1200" },
        };

        var existing = new HashSet<long> { 10, 12, 42 };
        var byApp = new Dictionary<int, IReadOnlyList<long>>
        {
            [1200] = [12],
            [42] = [10, 42],
        };

        var resolution = CatalogWishlistScopeQuery.Resolve(rows, existing, byApp);
        CheckEqual("10,12,42", string.Join(",", resolution.GameIds),
            "the decision keeps every attributed game once and invents none");
        CheckEqual(3, resolution.UnresolvedEntries,
            "negative id without fallback, zero app id and a case-mismatched store are three entries");
        CheckEqual(1, resolution.GameIds.Count(id => id == 12),
            "an app id shared by two rows is still one game");

        // A mapping that points at a game that does not exist cannot attribute anything.
        var orphanMapping = CatalogWishlistScopeQuery.Resolve(
            [new CatalogWishlistRow { GameId = null, Store = "steam", StoreGameId = "1200" }],
            new HashSet<long> { 10 },
            new Dictionary<int, IReadOnlyList<long>> { [1200] = [12] });
        CheckEqual(0, orphanMapping.GameIds.Count, "a mapping to a missing game attributes nothing");
        CheckEqual(1, orphanMapping.UnresolvedEntries, "and the row is counted instead");

        // No rows at all is not an error and not a count.
        var none = CatalogWishlistScopeQuery.Resolve([], new HashSet<long>(), new Dictionary<int, IReadOnlyList<long>>());
        CheckEqual(0, none.GameIds.Count, "an empty wishlist resolves to no game");
        CheckEqual(0, none.UnresolvedEntries, "an empty wishlist resolves to no unresolved entry");

        // The existence check of an empty claim list returns nothing instead of the whole catalog.
        CheckEqual(0, CatalogWishlistScopeQuery
            .ExistingGameIds(new List<Game> { GameRow(10, "Existente") }.AsQueryable(), [])
            .ToList()
            .Count, "an empty claim list checks no game");
    }

    /// <summary>
    /// Every canonical game is covered but the wishlist still holds entries the pass cannot attribute: "nothing
    /// pending" would be a half-truth, so the reason names the unresolved entries and the count travels with it.
    /// </summary>
    private static void CoveredWishlistWithUnresolvedEntries()
    {
        using var db = CreateContext();
        var provider = new RecordingSteamGameService();
        var service = new CatalogPriceRefreshService(
            new Repository(db),
            provider,
            new FixedClock(Now),
            NullLogger<CatalogPriceRefreshService>.Instance,
            new CatalogWishlistScopeResolver(new Repository(db)));

        // User 3 has no wishlist at all; add the covered-but-unresolved shape to the main fixture's requester by
        // using a wishlist that only holds a fresh game plus the unresolvable rows.
        db.UserLibrary.AddRange(
            Wished(7, EmptyUser, "steam", "110", gameId: 10),
            Wished(8, EmptyUser, "epic", "sin-id", gameId: null),
            Wished(9, EmptyUser, "steam", "5", gameId: 999));
        db.GameOffers.RemoveRange(db.GameOffers.Where(offer => offer.GameId == 10));
        db.GameOffers.Add(Offer(10, Now.AddHours(-1)));
        db.SaveChanges();

        var outcome = service.RefreshPendingAsync(EmptyUser, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual(0, outcome.Pending, "the resolved game is already covered");
        CheckEqual(0, outcome.Selected, "nothing selected when the canonical set is covered");
        CheckEqual(2, outcome.UnresolvedWishlistEntries, "the two unattributable entries are counted");
        CheckEqual(0, provider.Calls.Count, "no provider call for a covered wishlist");

        var details = Details(new CatalogPriceRefreshJobExecutor(service)
            .ExecuteAsync(JobRunOptions.Empty, EmptyUser, CancellationToken.None)
            .GetAwaiter()
            .GetResult());

        CheckEqual(
            "nothing_pending_unresolved_entries",
            details.GetProperty("reason").GetString(),
            "a covered wishlist with unresolved entries names them instead of claiming nothing is pending");
        CheckEqual(2, details.GetProperty("unresolvedWishlistEntries").GetInt32(),
            "the reason and the count travel together");
    }

    /// <summary>
    /// A wishlist whose every row lacks a usable identity is not an empty wishlist: the pass has nothing it can
    /// price, and it must say exactly that instead of claiming there was nothing pending.
    /// </summary>
    private static void AllUnresolvedWishlistIsReported()
    {
        using var db = CreateAllUnresolvedContext();
        var provider = new RecordingSteamGameService();
        var service = new CatalogPriceRefreshService(
            new Repository(db),
            provider,
            new FixedClock(Now),
            NullLogger<CatalogPriceRefreshService>.Instance,
            new CatalogWishlistScopeResolver(new Repository(db)));

        var outcome = service.RefreshPendingAsync(Requester, CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual(0, outcome.Selected, "an all-unresolved wishlist selects nothing");
        CheckEqual(0, outcome.Pending, "an all-unresolved wishlist has no canonical pending game");
        CheckEqual(3, outcome.UnresolvedWishlistEntries, "and it counts its three unattributable entries");
        CheckEqual(0, provider.Calls.Count, "an all-unresolved wishlist makes no provider call");

        var monitor = new CatalogFreshnessService(
            new Repository(db),
            new CatalogWishlistScopeResolver(new Repository(db)));
        var page = monitor.ListAsync(Requester, "all", 1, 20, Now, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        CheckEqual(0, page.Counts.Total, "no canonical game is counted");
        CheckEqual(0, page.Counts.Recent, "nothing is recent");
        CheckEqual(0, page.Counts.Stale, "nothing is stale");
        CheckEqual(0, page.Counts.Missing, "no game is invented as missing");
        CheckEqual(0, page.Total, "the page is empty");
        CheckEqual(3, page.UnresolvedWishlistEntries,
            "the monitor reports three entries it could not attribute, even with a zero canonical total");

        var details = Details(
            new CatalogPriceRefreshJobExecutor(service)
                .ExecuteAsync(JobRunOptions.Empty, Requester, CancellationToken.None)
                .GetAwaiter()
                .GetResult());

        Check(
            !string.Equals(details.GetProperty("reason").GetString(), "nothing_pending", StringComparison.Ordinal),
            "an all-unresolved wishlist is not reported as nothing pending");
        CheckEqual(
            "nothing_pending_unresolved_entries",
            details.GetProperty("reason").GetString(),
            "the reason names the unattributable entries");
        CheckEqual(3, details.GetProperty("unresolvedWishlistEntries").GetInt32(),
            "the job record carries the count");
    }

    /// <summary>
    /// The adversarial fixture: every way a wished row can fail to be attributed, next to the two ways it can
    /// succeed (a valid canonical id, and an exact Steam id for a row whose canonical id is orphaned).
    /// </summary>
    private static AppDbContext CreateAdversarialContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"catalog-unresolved-adversarial-{Guid.NewGuid():N}")
            .Options;

        var db = new AppDbContext(options, new OfflineUser());
        db.Games.AddRange(GameRow(10, "Canónica"), GameRow(12, "Por appid"));
        db.GameOffers.AddRange(Offer(10, Now.AddHours(-1)), Offer(12, Now.AddDays(-9)));
        db.SteamGames.AddRange(Steam(12, 1200, Now.AddDays(-9), steamGameId: 2001));

        db.UserLibrary.AddRange(
            Wished(1, Requester, "steam", "110", gameId: 10),
            Wished(2, Requester, "steam", "5", gameId: 999),
            Wished(3, Requester, "steam", "1200", gameId: null),
            Wished(4, Requester, "epic", "flip-1", gameId: null),
            Wished(5, Requester, "steam", "not-an-app-id", gameId: null),
            Wished(6, Requester, "steam", "424242", gameId: null),
            Wished(7, Requester, "steam", "1200", gameId: 888),
            Wished(8, Requester, "steam", "110", gameId: 10),
            Wished(9, OtherUser, "steam", "5", gameId: 999));

        db.SaveChanges();
        return db;
    }

    /// <summary>
    /// A wishlist that only holds rows the pass cannot attribute: two rows without any canonical or exact Steam
    /// identity plus one whose canonical id points at a game that does not exist. No game at all enters the
    /// scope, and no identity is guessed from the title.
    /// </summary>
    private static AppDbContext CreateAllUnresolvedContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"catalog-unresolved-checks-{Guid.NewGuid():N}")
            .Options;

        var db = new AppDbContext(options, new OfflineUser());
        db.Games.AddRange(GameRow(10, "Existente"));

        db.UserLibrary.AddRange(
            Wished(1, Requester, "epic", "floppy-knights", gameId: null),
            Wished(2, Requester, "steam", "not-an-app-id", gameId: null),
            Wished(3, Requester, "steam", "110", gameId: 999));

        db.SaveChanges();
        return db;
    }

    /// <summary>Renders a job result the way the operator sees it.</summary>
    private static System.Text.Json.JsonElement Details(JobExecutionResult result)
    {
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            result.Details,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The wishlist restriction must also translate to SQL, not only to LINQ-to-objects: the scoped game
    /// query keeps the <c>ANY</c> restriction and the resolver's Steam mapping stays a joinable query.
    /// </summary>
    private static void EfTranslationIsObserved()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=offline-checks;Port=1;Database=deals_checks;Username=checks;Password=checks;Timeout=1")
            .Options;
        using var db = new AppDbContext(options, new OfflineUser());
        var repository = new Repository(db);

        var scoped = CatalogFreshnessQueries.WishlistGames(repository.Get<Game>(), [10L, 12L]);
        var sql = Observe(scoped, "wishlist/games");
        Check(Has(sql, "= ANY"), "the wishlist scope is a bounded ANY restriction in SQL");

        var wishedRows = Observe(
            CatalogWishlistScopeQuery.WishedRows(repository.Get<UserLibrary>(), Requester),
            "wishlist/wished-rows");
        Check(Has(wishedRows, "user_id = "), "the wishlist rows are filtered by user in SQL");
        Check(Has(wishedRows, "state = "), "the wishlist rows are filtered by state in SQL");

        var mapped = Observe(
            CatalogWishlistScopeQuery.ResolvedGamesForAppIds(
                repository.Get<SteamGame>(),
                repository.Get<Game>(),
                [1200]),
            "wishlist/steam-mapping");
        Check(Has(mapped, "app_id"), "the exact Steam app id mapping stays in SQL");
        Check(Has(mapped, "region = "), "the Steam mapping is restricted to the MX snapshots");
        Check(Has(mapped, "JOIN"), "the Steam mapping is validated against existing games in SQL");

        var existing = Observe(
            CatalogWishlistScopeQuery.ExistingGameIds(repository.Get<Game>(), [10L, 999L]),
            "wishlist/existing-games");
        Check(Has(existing, "= ANY"), "the claimed canonical ids are checked for existence in SQL");
    }

    // --- Fixture ----------------------------------------------------------

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"catalog-wishlist-checks-{Guid.NewGuid():N}")
            .Options;

        var db = new AppDbContext(options, new OfflineUser());
        Seed(db);
        return db;
    }

    private static void Seed(AppDbContext db)
    {
        db.Games.AddRange(
            GameRow(10, "Deseado reciente"),
            GameRow(11, "Comprado"),
            GameRow(12, "Deseado por appid"),
            GameRow(13, "Steam-less deseado"),
            GameRow(20, "De otra cuenta"),
            GameRow(30, "Solo favorito"));

        db.GameOffers.AddRange(
            Offer(10, Now.AddHours(-2)),
            Offer(11, Now.AddDays(-10)),
            Offer(12, Now.AddDays(-9)),
            Offer(20, Now.AddDays(-8)),
            Offer(30, Now.AddDays(-7)));

        db.SteamGames.AddRange(
            Steam(10, 110, Now.AddHours(-2), steamGameId: 1001),
            Steam(11, 111, Now.AddDays(-10), steamGameId: 1002),
            Steam(12, 1200, Now.AddDays(-9), steamGameId: 1003),
            Steam(20, 200, Now.AddDays(-8), steamGameId: 1004),
            Steam(30, 300, Now.AddDays(-7), steamGameId: 1005));

        db.UserLibrary.AddRange(
            // The requester's wishlist: two canonical ids and one row that only an exact app id resolves.
            Wished(1, Requester, "steam", "110", gameId: 10),
            Wished(2, Requester, "steam", "1200", gameId: null),
            Wished(3, Requester, "steam", "130", gameId: 13),
            // A wished row with no exact identity at all: never guessed.
            Wished(4, Requester, "epic", "floppy-knights", gameId: null),
            // The library copy of an owned game, and another account's wishlist.
            Wished(5, Requester, "steam", "111", gameId: 11, state: LibraryStates.Owned),
            Wished(6, OtherUser, "steam", "200", gameId: 20));

        db.UserGameFavorites.AddRange(
            new UserGameFavorite { UserId = Requester, GameId = 11 },
            new UserGameFavorite { UserId = Requester, GameId = 30 });

        db.SaveChanges();
    }

    private static Game GameRow(long gameId, string title) =>
        new() { GameId = gameId, Title = title, NormalizedTitle = title };

    private static GameOffer Offer(long gameId, DateTime observedAt) =>
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
            OriginalRegularPriceMinor = 9_999,
            OriginalCurrentPriceMinor = 9_999,
            MxnRegularPriceMinor = 9_999,
            MxnCurrentPriceMinor = 9_999,
            PricingType = "regional",
            ObservedAt = observedAt,
        };

    private static SteamGame Steam(long gameId, int appId, DateTime observedAt, int steamGameId) =>
        new()
        {
            SteamGameId = steamGameId,
            AppId = appId,
            GameId = gameId,
            Region = "mx",
            Name = $"Juego {gameId}",
            Currency = "MXN",
            InitialPriceMinor = 9_999,
            CurrentPriceMinor = 9_999,
            ObservedAt = observedAt,
            OffersRefreshedAt = observedAt,
            GgDealsRefreshedAt = observedAt,
            EpicRefreshedAt = observedAt,
            MicrosoftRefreshedAt = observedAt,
        };

    private static UserLibrary Wished(
        long userLibraryId,
        int userId,
        string store,
        string storeGameId,
        long? gameId,
        string state = LibraryStates.Wished) =>
        new()
        {
            UserLibraryId = userLibraryId,
            UserId = userId,
            GameId = gameId,
            Store = store,
            StoreGameId = storeGameId,
            Title = $"{store}:{storeGameId}",
            State = state,
            ImportedAt = Now.AddDays(-30),
        };

    // --- Helpers ----------------------------------------------------------

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

    private static string? Observe(IQueryable query, string name)
    {
        try
        {
            var sql = query.ToQueryString();
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

    /// <summary>Fixed clock: every timestamp of the fixture is expressed relative to one instant.</summary>
    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    /// <summary>
    /// Provider double: records the app id it was asked to reprice and answers with an authoritative write so
    /// the counters have something real to classify. Every other member throws — the pass must never ask for
    /// details, search or discovery.
    /// </summary>
    private sealed class RecordingSteamGameService : ISteamGameService
    {
        public List<int> Calls { get; } = [];

        public Task<SteamStalePriceRefresh> RefreshStalePricesAsync(
            int appId,
            CancellationToken cancellationToken,
            int ageHours = GameRefreshPolicy.StalePriceBatchHours)
        {
            Calls.Add(appId);
            return Task.FromResult(new SteamStalePriceRefresh(
                Ran: true,
                SteamDue: true,
                DuePhases: [SteamStalePricePhases.Steam, SteamStalePricePhases.Itad],
                AdvancedPhases: [SteamStalePricePhases.Steam],
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

    private sealed class OfflineUser : ICurrentUserService
    {
        public int? GetUserId() => null;

        public int GetRequiredUserId() => throw new InvalidOperationException("wishlist checks have no request user");

        public int? GetSessionVersion() => null;

        public Guid? GetSessionId() => null;

        public string GetName() => "wishlist-checks";

        public string GetEmail() => "wishlist-checks@example.test";

        public bool IsAdmin() => false;
    }
}
