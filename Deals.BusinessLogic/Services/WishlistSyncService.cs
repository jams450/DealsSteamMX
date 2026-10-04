using System.Globalization;
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

    // Progress is persisted every this many games: the pass walks hundreds of appids over tens of minutes,
    // and a single save at the end threw all of it away whenever the process was restarted mid-pass.
    private const int SaveBatchSize = 25;

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
        // Both passes visit all wished rows, but snapshot reads stay bounded to distinct wished appids.
        var eligibleRows = await repository.GetTrack<UserLibrary>()
            .Where(entry => entry.Store == SteamStore && entry.State == WishedState)
            .ToListAsync(cancellationToken);
        var wishedAppIds = eligibleRows.Select(entry => ParseAppId(entry.StoreGameId))
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
        var gameRefreshTimes = candidateGames.ToDictionary(
            game => game.AppId,
            game => game.OffersRefreshedAt);
        eligibleRows = eligibleRows
            .OrderBy(entry => gameRefreshTimes.ContainsKey(ParseAppId(entry.StoreGameId)))
            .ThenBy(entry => gameRefreshTimes.GetValueOrDefault(ParseAppId(entry.StoreGameId)))
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
        var sinceSave = 0;

        // One refresh per app id: the detail snapshot is shared, so the same game on several users' lists
        // must not pay for the same provider calls twice.
        foreach (var providerPhase in new[] { false, true })
        {
            logger.LogInformation("Wishlist phase started: {Phase}", providerPhase ? "providers" : "steam");
            foreach (var group in eligibleRows.GroupBy(entry => ParseAppId(entry.StoreGameId)))
            {
                var appId = group.Key;
                if (appId <= 0)
                {
                    failed++;
                    continue;
                }

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

                        foreach (var entry in group)
                        {
                            entry.Title = details.Name;
                        }

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

                if (++sinceSave >= SaveBatchSize)
                {
                    // Checkpoint: an interrupted pass resumes from what is already persisted.
                    await repository.SaveChangesAsync();
                    sinceSave = 0;
                }
            }

            // Persist Steam phase before first external operation, even when no batch checkpoint fired.
            await repository.SaveChangesAsync();
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

    private async Task<(int Added, int Updated, int Removed, int FetchedFromSteam, int FetchFailed)> UpsertLibraryAsync(
        User user,
        IReadOnlyList<SteamWishlistItem> items,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetTrack<UserLibrary>()
            .Where(entry => entry.UserId == user.UserId && entry.Store == SteamStore && entry.State == WishedState)
            .ToListAsync(cancellationToken);
        var byStoreGameId = existing.ToDictionary(entry => entry.StoreGameId, StringComparer.Ordinal);

        // The wishlist payload carries no title, so a row is seeded with the name already known from
        // steam_games when the game was opened before; otherwise the appid is the placeholder until the
        // Steam fetch below (or the paced refresh pass) supplies the real name.
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

        var returned = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;

        foreach (var item in items)
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
        var obsolete = existing.Where(entry => !returned.Contains(entry.StoreGameId)).ToList();
        if (obsolete.Count > 0)
        {
            repository.GetTrack<UserLibrary>().RemoveRange(obsolete);
        }

        return (added, updated, obsolete.Count, fetchedFromSteam, fetchFailed);
    }
}
