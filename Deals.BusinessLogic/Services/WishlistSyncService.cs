using System.Globalization;
using Deals.BusinessLogic.Exceptions;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Settings handed to the sync service, mirroring the <see cref="SteamOffersSettings"/> pattern: the API
/// project binds the options section and hands the plain values down.
/// </summary>
public sealed record WishlistSyncSettings(int MaxRefreshesPerHour, int RefreshAfterDays, int MissingMetadataDelayMilliseconds);

/// <summary>
/// The per-user gate over <c>user_library</c> wishlist rows, shared by the Steam sync and the wishlist →
/// library acquisition (specs S3/L4). Both write the same two things — the wished row and the local
/// suppression that keeps it away — so they must not interleave: a sync that read the exclusions before an
/// acquisition wrote one would re-insert the row the acquisition had just deleted, and the game would come
/// back from the dead on the next run. Taking this gate before reading the exclusions and holding it until
/// the writes are saved makes "decide, then write" atomic per user.
///
/// <para>
/// It is a transaction-scoped advisory lock (<c>pg_advisory_xact_lock</c>), so the caller must already be
/// inside a transaction; nothing to release by hand and a rollback releases it. Deliberately one key per
/// user and not the global identity gate: the sync never creates canonical identity here, and serializing
/// every user's wishlist against every identity write would be a much wider blast radius.
/// </para>
/// </summary>
public static class WishlistUserLock
{
    /// <summary>
    /// Lock class of the per-user gate, used with PostgreSQL's two-argument
    /// <c>pg_advisory_xact_lock(class, id)</c> form: the key is exactly one pair per user, so two users can
    /// never share it and there is no arithmetic that could alias them.
    ///
    /// <para>
    /// It must stay disjoint from the other advisory keys this product takes: the wishlist-category lock uses
    /// the single-argument form with high word <c>0x574C00</c>, the identity gate hashes a constant name, and
    /// the hosted jobs use small numbers (<c>7_831_442_091</c> and neighbours). <c>0x574C01</c> is a different
    /// class, so a category write and a wishlist write never serialize against each other.
    /// </para>
    /// </summary>
    public const int LockClass = 0x574C01;

    /// <summary>
    /// Bounded wait for the request path: an acquisition waits at most
    /// <see cref="GameIdentityLock.WaitBudgetMilliseconds"/> and answers 409 instead of holding a pooled
    /// connection on a sync that is writing.
    /// </summary>
    public static async Task AcquireAsync(IRepository repository, int userId)
    {
        try
        {
            await repository.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = {GameIdentityLock.WaitBudgetMilliseconds}");
            await repository.ExecuteSqlRawAsync(AcquireSql(userId));
            await repository.ExecuteSqlRawAsync("SET LOCAL lock_timeout = 0");
        }
        catch (Npgsql.PostgresException exception) when (GameIdentityLock.IsLockTimeout(exception.SqlState))
        {
            throw new IdentityLockUnavailableException();
        }
    }

    /// <summary>
    /// Unbounded wait for the background sync: it holds the gate only over database work (the Steam detail
    /// calls happen before it), so waiting a moment for a person's own acquisition is better than dropping
    /// that user for the whole cycle.
    /// </summary>
    public static Task WaitAsync(IRepository repository, int userId) =>
        repository.ExecuteSqlRawAsync(AcquireSql(userId));

    private static string AcquireSql(int userId) =>
        $"SELECT pg_advisory_xact_lock({LockClass}, {userId})";
}

/// <summary>
/// Wishlist synchronization. The two passes are separate on purpose: <see cref="SyncListAsync"/> is a
/// quick metadata snapshot, while <see cref="RefreshWishedGamesAsync"/> walks the shared, rate-limited
/// price pipeline and can take tens of minutes.
/// </summary>
public sealed class WishlistSyncService(
    IRepository repository,
    ISteamWishlistClient wishlistClient,
    ISteamGameService steamGameService,
    WishlistSyncSettings settings,
    ILogger<WishlistSyncService> logger) : IWishlistSyncService
{
    private const string SteamStore = "steam";
    private const string WishedState = "wished";

    // The rest of the repo reads and writes the mx region snapshot; the wishlist enriches from the same one.
    private const string Region = "mx";

    public async Task<WishlistListSyncReport> SyncListAsync(CancellationToken cancellationToken)
    {
        // Tracked: the passed user rows carry wishlist_synced_at / wishlist_state, and the library rows
        // are upserted in place.
        var users = await repository.GetTrack<User>()
            .Where(user => user.SteamId64 != null && user.SteamId64 != string.Empty)
            .ToListAsync(cancellationToken);

        var state = WishlistStates.NoSteamId;
        var itemCount = 0;
        var added = 0;
        var updated = 0;
        var removed = 0;
        var fetchedFromSteam = 0;
        var fetchFailed = 0;
        DateTime? syncedAt = null;

        foreach (var user in users)
        {
            var result = await wishlistClient.GetWishlistAsync(user.SteamId64!, cancellationToken);

            if (result.Status == SteamWishlistStatus.Ok)
            {
                var (userAdded, userUpdated, userRemoved, userFetched, userFetchFailed) =
                    await UpsertLibraryAsync(user, result.Items, cancellationToken);
                added += userAdded;
                updated += userUpdated;
                removed += userRemoved;
                fetchedFromSteam += userFetched;
                fetchFailed += userFetchFailed;
                itemCount += result.Items.Count;

                user.WishlistSyncedAt = DateTime.UtcNow;
                user.WishlistState = WishlistStates.Ok;
            }
            else if (result.Status == SteamWishlistStatus.Inaccessible)
            {
                // No rows are deleted: a private profile is not an empty wishlist. The timestamp still
                // advances so "never synced" stays distinguishable from "private".
                user.WishlistSyncedAt = DateTime.UtcNow;
                user.WishlistState = WishlistStates.Inaccessible;
            }
            else
            {
                // Rate limited or degraded: persisted snapshot, timestamp and state are all left untouched
                // so the next cycle retries. Counted as a failure of this cycle.
                logger.LogWarning(
                    "[wishlist.list] {Status} for user {UserId}; persisted snapshot left untouched",
                    result.Status,
                    user.UserId);
            }

            state = WishlistStates.Compose(user.SteamId64, user.WishlistSyncedAt, user.WishlistState);
            syncedAt = user.WishlistSyncedAt;
        }

        await repository.SaveChangesAsync();

        return new WishlistListSyncReport(
            state,
            itemCount,
            added,
            updated,
            removed,
            syncedAt,
            fetchedFromSteam,
            fetchFailed);
    }

    public async Task<WishlistRefreshReport> RefreshWishedGamesAsync(CancellationToken cancellationToken)
    {
        // Untracked on purpose: this pass must never save entity state. It used to load the wished rows as
        // tracked entities and checkpoint them in batches, so a row an acquisition removed meanwhile made the
        // batch UPDATE touch zero rows — EF reports that as DbUpdateConcurrencyException and the whole price
        // pass failed because someone bought a game. The pass now writes titles with one targeted statement per
        // appid (see TitleUpdateScope), which matches no row instead.
        var wishedStoreGameIds = await repository.Get<UserLibrary>()
            .Where(entry => entry.Store == SteamStore && entry.State == WishedState)
            .Select(entry => entry.StoreGameId)
            .ToListAsync(cancellationToken);
        var wishedAppIds = wishedStoreGameIds.Select(ParseAppId)
            .Where(appId => appId > 0).ToHashSet();
        var candidateGames = await repository.Get<SteamGame>()
            .Where(game => game.Region == Region && wishedAppIds.Contains(game.AppId))
            .Select(game => new
            {
                game.AppId,
                game.ObservedAt,
                game.OffersRefreshedAt,
                game.GgDealsRefreshedAt,
                game.EpicRefreshedAt,
                game.MicrosoftRefreshedAt
            })
            .ToListAsync(cancellationToken);
        var snapshotsByAppId = candidateGames.ToDictionary(game => game.AppId);

        // Never-refreshed entries first, then the oldest offers snapshot. Missing steam_games entries sort first.
        // A non-numeric store id parses to 0 and is kept so it is counted as a failure, exactly as before.
        var gameRefreshTimes = candidateGames.ToDictionary(
            game => game.AppId,
            game => game.OffersRefreshedAt);
        var appIdsToRefresh = wishedStoreGameIds
            .Select(ParseAppId)
            .Distinct()
            .OrderBy(appId => gameRefreshTimes.ContainsKey(appId))
            .ThenBy(appId => gameRefreshTimes.GetValueOrDefault(appId))
            .ThenBy(appId => appId)
            .ToList();

        // MaxRefreshesPerHour is the real hourly ceiling, so the per-game spacing is 3600000/ceiling ms
        // (1000/h => 3.6 s, i.e. ~36 min for 600 games). This is the rate limit, not a suggestion.
        var pacing = TimeSpan.FromMilliseconds(3_600_000.0 / Math.Max(1, settings.MaxRefreshesPerHour));
        var refreshed = 0;
        var failed = 0;
        var providerStats = new Dictionary<string, ProviderCounters>(StringComparer.Ordinal)
        {
            ["steam"] = new(),
            ["itad"] = new(),
            ["ggdeals"] = new(),
            ["epic"] = new(),
            ["microsoft"] = new()
        };

        // One refresh per app id: the detail snapshot is shared, so the same game on several users' lists
        // must not pay for the same provider calls twice.
        foreach (var providerPhase in new[] { false, true })
        {
            logger.LogInformation("Wishlist phase started: {Phase}", providerPhase ? "providers" : "steam");
            foreach (var appId in appIdsToRefresh)
            {
                if (appId <= 0)
                {
                    failed++;
                    continue;
                }

                var appIdText = appId.ToString(CultureInfo.InvariantCulture);

                try
                {
                    // Steam TTL and independent provider TTLs are owned by their service operations.
                    var before = snapshotsByAppId.GetValueOrDefault(appId);
                    var details = providerPhase
                        ? await steamGameService.RefreshProvidersAsync(appId, cancellationToken)
                        : await steamGameService.GetByAppIdAsync(appId, forceRefresh: false, cancellationToken);
                    if (details is null)
                    {
                        failed++;
                        if (!providerPhase) providerStats["steam"].Failed++;
                    }
                    else
                    {
                        if (!providerPhase)
                            ClassifyProvider(providerStats["steam"], before?.ObservedAt, details.ObservedAt, stale: false);
                        else
                        {
                            logger.LogInformation("Wishlist provider stamps appId={AppId} itad={Itad} gg={Gg} epic={Epic} microsoft={Microsoft}",
                                appId, details.OffersRefreshedAt, details.GgDealsRefreshedAt, details.EpicRefreshedAt, details.MicrosoftRefreshedAt);
                            ClassifyProvider(providerStats["itad"], before?.OffersRefreshedAt, details.OffersRefreshedAt, details.OffersStale);
                            ClassifyProvider(providerStats["ggdeals"], before?.GgDealsRefreshedAt, details.GgDealsRefreshedAt, details.GgDealsStale);
                            var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(settings.RefreshAfterDays, 1, 90));
                            ClassifyProvider(providerStats["epic"], before?.EpicRefreshedAt, details.EpicRefreshedAt,
                                details.EpicRefreshedAt is null || details.EpicRefreshedAt < cutoff);
                            ClassifyProvider(providerStats["microsoft"], before?.MicrosoftRefreshedAt, details.MicrosoftRefreshedAt,
                                details.MicrosoftRefreshedAt is null || details.MicrosoftRefreshedAt < cutoff);
                        }

                        // The refreshed name, written where it belongs without loading any row. Rows the pass
                        // walked at the start that are gone by now (a dismissal, a merge) simply match nothing.
                        var titleScope = TitleUpdateScope(appIdText);
                        await repository.Get<UserLibrary>()
                            .Where(row => row.Store == titleScope.Store &&
                                row.State == titleScope.State &&
                                row.StoreGameId == titleScope.StoreGameId)
                            .ExecuteUpdateAsync(
                                setters => setters.SetProperty(row => row.Title, details.Name),
                                cancellationToken);

                        if (!providerPhase) refreshed++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // One game must never abort the cycle: the rest of the wishlist still gets refreshed.
                    failed++;
                    logger.LogWarning(exception, "[wishlist.refresh] app {AppId} failed", appId);
                }

                await Task.Delay(pacing, cancellationToken);
            }

            logger.LogInformation("Wishlist phase completed: {Phase}", providerPhase ? "providers" : "steam");
        }

        return new WishlistRefreshReport(
            refreshed,
            failed,
            providerStats.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToReport(),
                StringComparer.Ordinal));
    }

    private sealed class ProviderCounters
    {
        public int Attempted { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }

        public WishlistProviderRefreshOutcome ToReport() =>
            new(Attempted, Succeeded, Failed, Skipped);
    }

    private static void ClassifyProvider(
        ProviderCounters counters,
        DateTime? before,
        DateTime? after,
        bool stale)
    {
        if (before is null)
        {
            counters.Attempted++;
        }
        else if (after == before && !stale)
        {
            counters.Skipped++;
            return;
        }
        else
        {
            counters.Attempted++;
        }

        if (after is not null && !stale && (before is null || after > before))
        {
            counters.Succeeded++;
        }
        else
        {
            counters.Failed++;
        }
    }

    private static int ParseAppId(string storeGameId) =>
        int.TryParse(storeGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) ? appId : 0;

    /// <summary>
    /// Scope of the title write of the price-refresh pass: one targeted UPDATE over the rows that are still
    /// wished entries of that appid.
    ///
    /// <para>
    /// The pass used to mutate rows it had loaded as tracked entities and save them in batches. A row an
    /// acquisition removed in the meantime made that UPDATE touch zero rows, which EF reports as
    /// <c>DbUpdateConcurrencyException</c> — a background price pass failing because the person bought the
    /// game. A targeted statement over <c>(store, state, store_game_id)</c> simply matches nothing then: no
    /// stale entity, no exception, and nothing resurrected.
    /// </para>
    ///
    /// <para>
    /// The <c>state = 'wished'</c> filter is not decoration: an acquisition writes an owned/subscription row
    /// with the same store id, so a scope without it would rename the row the person just acquired.
    /// </para>
    /// </summary>
    public static (string Store, string State, string StoreGameId) TitleUpdateScope(string storeGameId)
        => (SteamStore, WishedState, storeGameId);

    /// <summary>
    /// Drops the wishlist entries the person removed locally (spec S3): a game acquired and dismissed is
    /// stored in <c>wishlist_exclusions</c>, and Steam keeps returning it until the person removes it there
    /// too, so the sync must not put the row back. Pure and public so the rule is checkable without a
    /// database (<c>checks/WishlistAcquisition.Checks</c>); matching is ordinal on the store id, which is the
    /// appid as text for a Steam entry.
    /// </summary>
    public static IReadOnlyList<SteamWishlistItem> WithoutExcludedItems(
        IReadOnlyList<SteamWishlistItem> items,
        IReadOnlySet<string> excludedStoreGameIds)
    {
        if (excludedStoreGameIds.Count == 0)
        {
            return items;
        }

        return items
            .Where(item => !excludedStoreGameIds.Contains(item.AppId.ToString(CultureInfo.InvariantCulture)))
            .ToList();
    }

    private async Task<(int Added, int Updated, int Removed, int FetchedFromSteam, int FetchFailed)> UpsertLibraryAsync(
        User user,
        IReadOnlyList<SteamWishlistItem> items,
        CancellationToken cancellationToken)
    {
        // Phase 1, no gate: the titles this payload cannot name yet. The wishlist payload carries no title, so
        // a row is seeded with the name already known from steam_games when the game was opened before;
        // otherwise the appid is the placeholder until the Steam fetch below (or the paced refresh pass)
        // supplies the real name. These calls are the slow part of the sync, so they run BEFORE the per-user
        // gate is taken: holding that gate across hundreds of HTTP calls would block the person's own
        // acquisitions, and it protects the rows, not the snapshot.
        var appIds = items.Select(item => item.AppId).Distinct().ToList();
        var knownNames = appIds.Count == 0
            ? new Dictionary<int, string>()
            : await repository.Get<SteamGame>()
                .Where(game => game.Region == Region && appIds.Contains(game.AppId))
                .Select(game => new { game.AppId, game.Name })
                .ToDictionaryAsync(game => game.AppId, game => game.Name, cancellationToken);

        // App ids with no steam_games row yet: the list still owes them a real title, and the later price
        // pass needs a row to enrich. An appid that already has a row is never touched here.
        // ponytail: no pacing here, unlike RefreshWishedGamesAsync: one sequential Steam call per game
        // missing from the snapshot, on a user-triggered sync. Add a delay (or reuse MaxRefreshesPerHour)
        // only if Steam actually answers 429 in practice.
        var fetchedFromSteam = 0;
        var fetchFailed = 0;
        var missingAppIds = appIds.Where(candidate => !knownNames.ContainsKey(candidate)).ToList();
        foreach (var appId in missingAppIds)
        {
            try
            {
                // Steam only: GetAppDetailsOnlyAsync persists name and price without running ITAD, gg.deals
                // or bundles, and without touching any refresh timestamp.
                var details = await steamGameService.GetAppDetailsOnlyAsync(appId, cancellationToken);
                if (details is null)
                {
                    fetchFailed++;
                    continue;
                }

                knownNames[appId] = details.Name;
                fetchedFromSteam++;

                if (settings.MissingMetadataDelayMilliseconds > 0)
                {
                    await Task.Delay(settings.MissingMetadataDelayMilliseconds, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One appid Steam cannot answer for must never abort the whole list sync.
                fetchFailed++;
                logger.LogWarning(exception, "[wishlist.list] steam details for app {AppId} failed", appId);
            }
        }

        // Phase 2, gated and transactional: everything that writes a wished row. The exclusions are read
        // INSIDE the per-user gate the acquisition also takes (specs S3/L4), so a sync that started before an
        // acquisition cannot re-insert the row that acquisition deleted — the acquisition either lands first
        // and this read sees its exclusion, or it waits for this user's writes to commit.
        return await repository.ExecuteInTransactionAsync(async () =>
        {
            await WishlistUserLock.WaitAsync(repository, user.UserId);

            var excludedStoreGameIds = (await repository.Get<WishlistExclusion>()
                    .Where(exclusion => exclusion.UserId == user.UserId && exclusion.Store == SteamStore)
                    .Select(exclusion => exclusion.StoreGameId)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            var incoming = WithoutExcludedItems(items, excludedStoreGameIds);

            // Read under the gate: these are the rows the write below upserts and prunes.
            var existing = await repository.GetTrack<UserLibrary>()
                .Where(entry => entry.UserId == user.UserId && entry.Store == SteamStore && entry.State == WishedState)
                .ToListAsync(cancellationToken);
            var byStoreGameId = existing.ToDictionary(entry => entry.StoreGameId, StringComparer.Ordinal);

            var returned = new HashSet<string>(StringComparer.Ordinal);
            var added = 0;
            var updated = 0;

            foreach (var item in incoming)
            {
                var storeGameId = item.AppId.ToString(CultureInfo.InvariantCulture);
                if (!returned.Add(storeGameId))
                {
                    // A duplicated appid in the payload keeps the first entry.
                    continue;
                }

                var addedAt = item.AddedAt?.UtcDateTime;
                var knownName = knownNames.GetValueOrDefault(item.AppId);

                if (!byStoreGameId.TryGetValue(storeGameId, out var entry))
                {
                    repository.GetTrack<UserLibrary>().Add(new UserLibrary
                    {
                        UserId = user.UserId,
                        Store = SteamStore,
                        StoreGameId = storeGameId,
                        Title = knownName ?? storeGameId,
                        State = WishedState,
                        Priority = item.Priority,
                        AddedAt = addedAt,
                        ImportedAt = DateTime.UtcNow
                    });
                    added++;
                    continue;
                }

                // Steam priority is authoritative only when the row is first imported. Local edits survive future syncs.
                entry.AddedAt = addedAt;
                entry.ImportedAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(knownName))
                {
                    entry.Title = knownName;
                }

                updated++;
            }

            // Only the wished rows of this user/store are pruned: owned entries and other stores are never touched.
            // An excluded appid never enters `returned`, so a row that survived an earlier cycle is pruned here.
            var obsolete = existing.Where(entry => !returned.Contains(entry.StoreGameId)).ToList();
            if (obsolete.Count > 0)
            {
                repository.GetTrack<UserLibrary>().RemoveRange(obsolete);
            }

            // Inside the transaction on purpose: the commit must contain the rows, not leave them queued in the
            // change tracker until the caller's save.
            await repository.SaveChangesAsync(cancellationToken);

            return (added, updated, obsolete.Count, fetchedFromSteam, fetchFailed);
        });
    }
}
