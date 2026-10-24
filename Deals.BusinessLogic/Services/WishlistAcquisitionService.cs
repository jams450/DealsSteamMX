using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Catalog;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Wishlist → library acquisition (specs S1/S2/S3). Every decision lives in
/// <see cref="WishlistAcquisitionRules"/>; this class only loads the facts those rules need and executes the
/// plan they return, inside one transaction that starts with two gates in a fixed order.
///
/// <para>
/// <see cref="WishlistUserLock"/> comes first and serializes this acquisition with the Steam sync and with the
/// user's own acquisitions, so "is it still wished?" and "is an exclusion already stored?" cannot be answered
/// from a snapshot another writer is about to invalidate. <see cref="GameIdentityLock"/> comes second and
/// serializes it with every identity writer, which is what makes canonical facts read *after* it trustworthy:
/// a merge that repointed or deleted the canonical row while this request waited is already committed, and
/// every read below is newer than the merge. The same pair of gates is what makes "check, then insert" safe
/// for <c>uq_user_library</c>; the cost is two short waits and a handful of indexed reads.
/// </para>
/// </summary>
public sealed class WishlistAcquisitionService(
    IRepository repository,
    IGameIdentityResolver identityResolver) : IWishlistAcquisitionService
{
    // The wishlist is a Steam list: its rows live under store = 'steam' with the appid as store id.
    private const string WishlistStore = GameExternalIdNamespaces.Steam;

    // The region whose snapshot the rest of the product reads first.
    private const string Region = "mx";

    public async Task<WishlistAcquisitionResult?> AcquireAsync(
        int userId,
        int appId,
        WishlistAcquisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (appId <= 0)
        {
            throw new ArgumentException("El AppID debe ser positivo", nameof(appId));
        }

        // Validation is pure and happens before the transaction opens: an invalid payload must not hold the
        // identity gate while it is rejected.
        var input = WishlistAcquisitionRules.Validate(request, DateOnly.FromDateTime(DateTime.UtcNow));
        var appIdText = appId.ToString(CultureInfo.InvariantCulture);

        return await repository.ExecuteInTransactionAsync<WishlistAcquisitionResult?>(async () =>
        {
            // One lock order, always: the per-user wishlist gate first, then the global identity gate.
            //
            // The user gate serializes this acquisition with the Steam sync (which reads its exclusions and
            // writes wished rows under the same key) and with the user's own other acquisitions. The identity
            // gate is what a merge holds while it repoints or deletes canonical rows, so it must be taken
            // BEFORE any canonical fact is read: a request that waited here and then trusted values loaded
            // earlier would resolve identity against a row the merge had already reassigned. That is why every
            // read below happens after both gates — including the plain 404 case, which is cheap and keeps the
            // order uniform.
            await WishlistUserLock.AcquireAsync(repository, userId);
            await GameIdentityLock.AcquireAsync(repository);

            var wished = await repository.GetTrack<UserLibrary>()
                .FirstOrDefaultAsync(
                    row => row.UserId == userId &&
                        row.Store == WishlistStore &&
                        row.State == LibraryStates.Wished &&
                        row.StoreGameId == appIdText,
                    cancellationToken);
            if (wished is null)
            {
                // The row is gone: either this user never wished the appid (404) or the first run of this very
                // submit already acquired it and dismissed the row (L4 idempotency). The replay path is
                // read-only and writes nothing.
                return await ResolveDismissedReplayAsync(userId, appId, appIdText, input, cancellationToken);
            }

            // Fresh under the gates: the Steam snapshot of the appid.
            var snapshot = await LoadSnapshotAsync(appId, cancellationToken);

            // Display title for anything this request may create. Steam's snapshot name is the best source; the
            // wished row's own name is the fallback for the legitimate case of an appid Steam never answered
            // for. The appid placeholder is not a name, so that case stays a 404 instead of creating a
            // permanently misnamed canonical game.
            var title = WishlistAcquisitionRules.ResolveAcquisitionTitle(snapshot?.Name, wished.Title, appIdText);

            var canonicalGameId = await ResolveCanonicalGameIdAsync(appIdText, wished, snapshot, title, cancellationToken);
            if (canonicalGameId is null)
            {
                // No exact Steam identity and no name to create one with.
                return null;
            }

            if (snapshot is not null && WishlistAcquisitionRules.ShouldBackfillCanonicalId(snapshot.GameId))
            {
                // Progressive backfill, exactly like the next Steam detail write would do. Only a null link is
                // filled: an existing one belongs to the merge writer.
                snapshot.GameId = canonicalGameId;
            }

            if (WishlistAcquisitionRules.ShouldBackfillCanonicalId(wished.GameId))
            {
                wished.GameId = canonicalGameId;
            }

            // Exact ids this platform already knows for the game. Only consulted for a platform other than the
            // wishlist's own store, whose id is already exact.
            var platformExternalIds = string.Equals(input.Platform, wished.Store, StringComparison.Ordinal)
                ? []
                : await repository.Get<GameExternalId>()
                    .Where(external =>
                        external.GameId == canonicalGameId.Value && external.NamespaceName == input.Platform)
                    .Select(external => external.ExternalId)
                    .ToListAsync(cancellationToken);

            // The row key of the acquisition is decided by the rules, so the duplicate lookup uses the same id
            // the row would be written with.
            var storeGameId = WishlistAcquisitionRules.ResolveStoreGameId(
                input.Platform,
                canonicalGameId.Value,
                wished.Store,
                wished.StoreGameId,
                platformExternalIds);

            var existingLibraryRow = await FindExistingRowAsync(
                userId, input.Platform, canonicalGameId.Value, storeGameId, cancellationToken);

            var facts = new WishlistAcquisitionFacts(
                WishedStore: wished.Store,
                WishedStoreGameId: wished.StoreGameId,
                CanonicalGameId: canonicalGameId.Value,
                // The resolved display title, with the appid as the last resort — the same placeholder the list
                // sync writes when Steam has no name yet, never an invented one.
                Title: title ?? appIdText,
                ExistingRow: existingLibraryRow is null
                    ? null
                    : new WishlistAcquisitionExistingRow(
                        existingLibraryRow.UserLibraryId,
                        existingLibraryRow.State,
                        HasAcquisitionMetadata(existingLibraryRow)),
                PlatformExternalIds: platformExternalIds,
                AlreadyExcluded: await repository.Get<WishlistExclusion>()
                    .AnyAsync(
                        exclusion => exclusion.UserId == userId &&
                            exclusion.Store == wished.Store &&
                            exclusion.StoreGameId == wished.StoreGameId,
                        cancellationToken));

            var plan = WishlistAcquisitionRules.Plan(input, facts, DateTime.UtcNow);

            // The single claim this feature may write: the wishlist row's own exact store id. A canonical game
            // id is never claimed as an external id in a real store namespace. A refusal would mean the pair
            // already belongs to another canonical game — impossible here, because the canonical id was read
            // from that very mapping under the identity gate — so abort instead of linking the wrong game.
            if (WishlistAcquisitionRules.ClaimFor(input.Platform, facts.WishedStore, facts.WishedStoreGameId) is { } claim)
            {
                var claimed = await GameIdentityClaimer.TryClaimAsync(
                    repository, facts.CanonicalGameId, claim.Namespace, claim.ExternalId, cancellationToken);
                if (!claimed)
                {
                    throw new InvalidOperationException(
                        $"El mapeo ({claim.Namespace}, {claim.ExternalId}) pertenece a otro juego canónico.");
                }
            }

            var userLibraryId = plan.UserLibraryId;
            if (plan.CreateRow)
            {
                var now = DateTime.UtcNow;
                var row = new UserLibrary
                {
                    UserId = userId,
                    Store = plan.Store,
                    StoreGameId = plan.StoreGameId,
                    Title = plan.Title,
                    State = plan.State,
                    AddedAt = plan.AddedAt,
                    ImportedAt = now,
                    GameId = plan.GameId,
                    AcquiredStore = plan.AcquiredStore,
                    AcquisitionType = plan.AcquisitionType,
                    // The two source facts are optional and ride with the rest of the metadata: the person's
                    // selection is stored as sent, and "not selected" stays NULL instead of a guess.
                    AcquiredStoreType = plan.AcquiredStoreType,
                    AcquisitionProvider = plan.AcquisitionProvider,
                    AcquisitionDate = plan.AcquisitionDate
                };
                repository.GetTrack<UserLibrary>().Add(row);
                await repository.SaveChangesAsync(cancellationToken);
                userLibraryId = row.UserLibraryId;
            }
            else if (plan.FillAcquisitionMetadata && existingLibraryRow is not null)
            {
                // Filled once: an imported row with no acquisition metadata receives it, and every later submit
                // leaves it alone. The entity is tracked (GetTrack), so the change is flushed by the save below.
                // The whole unit is written here, source facts included; this branch is the only place where an
                // existing row can receive them, and it cannot run twice for the same row.
                existingLibraryRow.AcquiredStore = plan.AcquiredStore;
                existingLibraryRow.AcquisitionType = plan.AcquisitionType;
                existingLibraryRow.AcquiredStoreType = plan.AcquiredStoreType;
                existingLibraryRow.AcquisitionProvider = plan.AcquisitionProvider;
                existingLibraryRow.AcquisitionDate = plan.AcquisitionDate;
            }

            if (plan.InsertExclusion)
            {
                repository.GetTrack<WishlistExclusion>().Add(new WishlistExclusion
                {
                    UserId = userId,
                    Store = facts.WishedStore,
                    StoreGameId = facts.WishedStoreGameId
                });
            }

            if (plan.Dismiss)
            {
                // The local removal: the wished row goes, and its wishlist_category_items go with it through
                // fk_wishlist_category_items_library_user (ON DELETE CASCADE on user_id + user_library_id).
                // Nothing is ever sent to Steam, so the person still has to remove the game there.
                repository.GetTrack<UserLibrary>().Remove(wished);
            }

            await repository.SaveChangesAsync(cancellationToken);

            return new WishlistAcquisitionResult(plan.Outcome, userLibraryId, plan.GameId, plan.Dismiss);
        });
    }

    /// <summary>
    /// Read-only answer for a repeated submit whose first run already dismissed the wished row (S3/L4). A
    /// repeated submit must not become a 404 just because it succeeded: the local exclusion proves the appid
    /// was acquired here, and the chosen platform must already hold the game, so the answer is the same
    /// <c>already_owned</c> with the same row id and <c>dismissed = true</c>. Without both facts the route
    /// still answers 404 — this door never acquires an appid the person did not wish.
    ///
    /// <para>
    /// Nothing is written here: the first run created the row, wrote the exclusion and deleted the wished row,
    /// and this path must not claim, backfill or reshape any of it.
    /// </para>
    /// </summary>
    private async Task<WishlistAcquisitionResult?> ResolveDismissedReplayAsync(
        int userId,
        int appId,
        string appIdText,
        WishlistAcquisitionInput input,
        CancellationToken cancellationToken)
    {
        var dismissed = await repository.Get<WishlistExclusion>()
            .AnyAsync(
                exclusion => exclusion.UserId == userId &&
                    exclusion.Store == WishlistStore &&
                    exclusion.StoreGameId == appIdText,
                cancellationToken);
        if (!dismissed)
        {
            return null;
        }

        // Exact ids only: the mapping, or the snapshot's backfilled canonical id. No identity is created on a
        // replay, because this path writes nothing.
        var mappedGameId = await repository.Get<GameExternalId>()
            .Where(external =>
                external.NamespaceName == GameExternalIdNamespaces.Steam && external.ExternalId == appIdText)
            .Select(external => (long?)external.GameId)
            .FirstOrDefaultAsync(cancellationToken);
        var snapshot = await LoadSnapshotAsync(appId, cancellationToken);
        var canonicalGameId = WishlistAcquisitionRules.ResolveCanonicalGameId(mappedGameId, snapshot?.GameId, null);
        if (canonicalGameId is not { } gameId)
        {
            return null;
        }

        var storeGameId = string.Equals(input.Platform, WishlistStore, StringComparison.Ordinal)
            ? appIdText
            : gameId.ToString(CultureInfo.InvariantCulture);
        var existing = await FindExistingRowAsync(userId, input.Platform, gameId, storeGameId, cancellationToken);

        return WishlistAcquisitionRules.Replay(
            gameId,
            existing is null
                ? null
                : new WishlistAcquisitionExistingRow(
                    existing.UserLibraryId,
                    existing.State,
                    HasAcquisitionMetadata(existing)),
            alreadyExcluded: true);
    }

    /// <summary>
    /// Canonical identity of one wished appid, from exact ids only: the claimed Steam mapping first (a merge
    /// repoints it, so it is the identity truth), then the snapshot's backfilled <c>game_id</c>, then the wished
    /// row. When none of them exists, the canonical row is created through the shared resolver from the exact
    /// <c>('steam', appid)</c> pair — identity still comes from that id, never from a title match. The title is
    /// display data only, and it is sufficient for the legitimate case of an appid Steam has not answered for:
    /// the wished row's own name names the new game, with no external call.
    /// </summary>
    private async Task<long?> ResolveCanonicalGameIdAsync(
        string appIdText,
        UserLibrary wished,
        SteamGame? snapshot,
        string? title,
        CancellationToken cancellationToken)
    {
        var mappedGameId = await repository.Get<GameExternalId>()
            .Where(external =>
                external.NamespaceName == GameExternalIdNamespaces.Steam && external.ExternalId == appIdText)
            .Select(external => (long?)external.GameId)
            .FirstOrDefaultAsync(cancellationToken);

        var resolved = WishlistAcquisitionRules.ResolveCanonicalGameId(mappedGameId, snapshot?.GameId, wished.GameId);
        if (resolved is not null)
        {
            return resolved;
        }

        // No name means the only thing available was the appid placeholder: creating a game with it would leave
        // a permanently misnamed canonical row, so this stays a 404 until Steam supplies a real name.
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var game = await identityResolver.ResolveOrCreateGameAsync(
            new GameIdentityRequest(title, [new GameExternalIdRef(GameExternalIdNamespaces.Steam, appIdText)]),
            cancellationToken);
        return game.GameId;
    }

    /// <summary>
    /// The Steam snapshot for the appid, preferring the region the rest of the product reads. Null when Steam
    /// never answered for this appid.
    /// </summary>
    private async Task<SteamGame?> LoadSnapshotAsync(int appId, CancellationToken cancellationToken)
    {
        var snapshots = await repository.GetTrack<SteamGame>()
            .Where(game => game.AppId == appId)
            .ToListAsync(cancellationToken);

        return snapshots
            .OrderBy(game => string.Equals(game.Region, Region, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(game => game.Region, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The owned or subscription row that already covers this game on the chosen platform, matched by
    /// canonical identity (or by the platform's own store id, which is identity inside one store). Ordered by
    /// row id so a legacy duplicate answers the same row every time.
    /// </summary>
    private async Task<UserLibrary?> FindExistingRowAsync(
        int userId,
        string platform,
        long canonicalGameId,
        string storeGameId,
        CancellationToken cancellationToken)
    {
        return await repository.GetTrack<UserLibrary>()
            .Where(row => row.UserId == userId &&
                row.Store == platform &&
                (row.State == LibraryStates.Owned || row.State == LibraryStates.Subscription) &&
                (row.GameId == canonicalGameId || row.StoreGameId == storeGameId))
            .OrderBy(row => row.UserLibraryId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// True when the row already records an acquisition, which is what stops every later submit from
    /// rewriting it. Only the shop and the type take part: the optional source facts
    /// (<c>acquired_store_type</c>, <c>acquisition_provider</c>) are deliberately left out, because treating
    /// their NULL as "incomplete" would reopen the fill for every row written before they existed and let a
    /// repeat overwrite the shop and type that acquisition already established. Their NULL is a valid answer.
    /// </summary>
    private static bool HasAcquisitionMetadata(UserLibrary row) =>
        row.AcquisitionType is not null && row.AcquiredStore is not null;
}
