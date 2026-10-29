using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Catalog;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Atomic bundle purchase and import (specs S3-S6). Every decision lives in
/// <see cref="BundlePurchaseRules"/>; this service only loads the facts, resolves the canonical identity
/// the plan names and executes the writes inside one transaction.
///
/// <para>
/// The transaction starts with the same two gates, in the same order, as the wishlist → library
/// acquisition: <see cref="WishlistUserLock"/> (per user) first, because this purchase writes the same
/// wished rows and exclusions the Steam sync writes, then <see cref="GameIdentityLock"/> (global), because
/// the ITAD UUID resolution may create canonical rows and mappings. Both gates are held for the whole
/// unit, so a repeated or concurrent confirm of the same bundle either sees the stored purchase or waits
/// for it — the unique <c>(user_id, source, bundle_key)</c> key is the invariant behind that guarantee.
/// </para>
///
/// <para>
/// The provider snapshot is frozen on the purchase row before the first library write: the tier payload
/// the buyer confirmed, its hash and the per-item membership are stored, so a later upstream refresh can
/// never rewrite what was bought. Identity is ITAD UUID (<c>itad</c> namespace) or an explicit manual
/// canonical link; a title is never matched, and no Steam appid is ever invented.
/// </para>
/// </summary>
public sealed class BundlePurchaseService(
    IRepository repository,
    IGameIdentityResolver identityResolver) : IBundlePurchaseService
{
    private const string SteamStore = StoreKeys.Steam;

    // The two library states that mean "this user already has the game on that platform". A subscription
    // is a catalogue entry, not a purchase, but importing an owned row beside it would duplicate the game.
    private static readonly string[] OwnedStates = [LibraryStates.Owned, LibraryStates.Subscription];

    public async Task<BundlePurchasePreview?> PreviewAsync(
        int userId,
        string? source,
        string? bundleKey,
        string? platform,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(source, bundleKey, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var itadCanonical = await LoadItadCanonicalIdsAsync(snapshot, cancellationToken);
        var ownedRows = await LoadOwnedRowsAsync(userId, platform, cancellationToken);
        var purchase = await FindPurchaseAsync(userId, snapshot.Source, snapshot.BundleKey, cancellationToken);
        var preview = BundlePurchaseRules.BuildPreview(snapshot, platform, itadCanonical, ownedRows);

        return preview with
        {
            Purchased = purchase is not null,
            PurchaseId = purchase?.BundlePurchaseId
        };
    }

    public async Task<BundlePurchaseResult?> PurchaseAsync(
        int userId,
        BundlePurchaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await repository.ExecuteInTransactionAsync<BundlePurchaseResult?>(async () =>
        {
            // One lock order, always: the per-user wishlist gate first, then the global identity gate.
            await WishlistUserLock.AcquireAsync(repository, userId);
            await GameIdentityLock.AcquireAsync(repository);

            // Everything below is read after both gates, so the snapshot, the membership and the canonical
            // mappings cannot move underneath the decision.
            var snapshot = await LoadSnapshotAsync(request.Source, request.BundleKey, cancellationToken);
            if (snapshot is null)
            {
                return null;
            }

            // A purchase of the same bundle through the sibling ITAD source is the same purchase: the
            // catalog already treats them as one family, so the replay answer is the stored row.
            var existingPurchase = await FindPurchaseAsync(userId, snapshot.Source, snapshot.BundleKey, cancellationToken);
            if (existingPurchase is not null)
            {
                return await BuildResultAsync(
                    existingPurchase,
                    await LoadPurchaseGamesAsync(existingPurchase.BundlePurchaseId, cancellationToken),
                    created: false,
                    alreadyReported: true,
                    wishlistRemovedCount: null,
                    cancellationToken);
            }

            var existingCanonicalGameIds = await LoadExistingCanonicalIdsAsync(request, cancellationToken);
            var plan = BundlePurchaseRules.Plan(
                request,
                snapshot,
                userId,
                DateOnly.FromDateTime(DateTime.UtcNow),
                existingCanonicalGameIds);

            var purchase = new BundlePurchase
            {
                UserId = plan.UserId,
                Source = plan.Source,
                BundleKey = plan.BundleKey,
                Title = plan.Title,
                ShopId = plan.ShopId,
                ShopName = plan.ShopName,
                PageUrl = plan.PageUrl,
                DealUrl = plan.DealUrl,
                PublishedAt = plan.PublishedAt,
                ExpiresAt = plan.ExpiresAt,
                ObservedAt = plan.ObservedAt,
                TierKey = plan.TierKey,
                TierIndex = plan.TierIndex,
                TierPriceMinor = plan.TierPriceMinor,
                TierCurrency = plan.TierCurrency,
                TierAddon = plan.TierAddon,
                TierItemsComplete = plan.TierItemsComplete,
                TierSelectionCount = plan.TierSelectionCount,
                SelectionMode = plan.SelectionMode,
                SelectedCount = plan.SelectedCount,
                Platform = plan.Platform,
                AcquiredAt = plan.AcquiredAt,
                AcquiredStore = plan.AcquiredStore,
                AcquiredStoreType = plan.AcquiredStoreType,
                AcquisitionProvider = plan.AcquisitionProvider,
                SnapshotHash = plan.SnapshotHash,
                SnapshotJson = plan.SnapshotJson
            };
            repository.GetTrack<BundlePurchase>().Add(purchase);
            await repository.SaveChangesAsync(cancellationToken);

            // Identity is resolved for the whole bundle before any library row is written: the Steam appids
            // are needed to key a Steam row honestly (never a bare canonical id) and to clear the wishlist.
            var identities = await ResolveIdentitiesAsync(plan, cancellationToken);
            var steamAppIds = await LoadSteamAppIdsAsync(
                identities.Select(item => item.GameId).Distinct().ToList(),
                cancellationToken);
            var resolved = await ImportAsync(purchase, identities, steamAppIds, cancellationToken);
            var wishlist = await RemoveWishlistAsync(userId, resolved, steamAppIds, cancellationToken);

            var purchaseGames = new List<BundlePurchaseGame>(resolved.Count);
            foreach (var item in resolved)
            {
                var purchaseGame = new BundlePurchaseGame
                {
                    BundlePurchaseId = purchase.BundlePurchaseId,
                    ItemKey = item.ItemKey,
                    Title = Truncate(item.Title),
                    ItemType = item.Type,
                    ItadGameId = item.ItadGameId,
                    GameId = item.GameId,
                    CanonicalSource = item.CanonicalSource,
                    Platform = item.Platform,
                    Outcome = item.Outcome,
                    UserLibraryId = item.UserLibraryId,
                    WishlistRemoved = item.SteamAppId is not null && wishlist.Removals.Contains(item.SteamAppId)
                };
                purchaseGames.Add(purchaseGame);
                repository.GetTrack<BundlePurchaseGame>().Add(purchaseGame);
            }

            await repository.SaveChangesAsync(cancellationToken);

            return await BuildResultAsync(
                purchase,
                purchaseGames,
                created: true,
                alreadyReported: false,
                wishlistRemovedCount: purchaseGames.Count(game => game.WishlistRemoved),
                cancellationToken);
        });
    }

    public async Task<IReadOnlyList<BundlePurchaseRecord>> ListAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        var purchases = await repository.Get<BundlePurchase>()
            .Where(purchase => purchase.UserId == userId)
            .OrderByDescending(purchase => purchase.Created)
            .ThenByDescending(purchase => purchase.BundlePurchaseId)
            .ToListAsync(cancellationToken);
        if (purchases.Count == 0)
        {
            return [];
        }

        var ids = purchases.Select(purchase => purchase.BundlePurchaseId).ToList();
        var gamesByPurchase = (await repository.Get<BundlePurchaseGame>()
                .Where(game => ids.Contains(game.BundlePurchaseId))
                .OrderBy(game => game.BundlePurchaseGameId)
                .ToListAsync(cancellationToken))
            .GroupBy(game => game.BundlePurchaseId)
            .ToDictionary(group => group.Key, group => group.ToList());

        return purchases
            .Select(purchase => ToRecord(
                purchase,
                gamesByPurchase.GetValueOrDefault(purchase.BundlePurchaseId) ?? []))
            .ToList();
    }

    public async Task<IReadOnlyList<BundlePurchaseDuplicateEntry>> ListDuplicatesAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        var purchases = await repository.Get<BundlePurchase>()
            .Where(purchase => purchase.UserId == userId)
            .ToListAsync(cancellationToken);
        if (purchases.Count == 0)
        {
            return [];
        }

        var ids = purchases.Select(purchase => purchase.BundlePurchaseId).ToList();
        var duplicates = await repository.Get<BundlePurchaseGame>()
            .Where(game => ids.Contains(game.BundlePurchaseId) &&
                game.Outcome == BundlePurchaseOutcomes.Duplicate)
            .OrderBy(game => game.BundlePurchaseGameId)
            .ToListAsync(cancellationToken);
        if (duplicates.Count == 0)
        {
            return [];
        }

        var byId = purchases.ToDictionary(purchase => purchase.BundlePurchaseId);
        var libraryTitles = await LoadLibraryTitlesAsync(
            duplicates.Where(game => game.UserLibraryId is not null)
                .Select(game => game.UserLibraryId!.Value)
                .Distinct()
                .ToList(),
            userId,
            cancellationToken);

        return duplicates
            .Where(game => byId.ContainsKey(game.BundlePurchaseId))
            .Select(game =>
            {
                var purchase = byId[game.BundlePurchaseId];
                return new BundlePurchaseDuplicateEntry(
                    purchase.BundlePurchaseId,
                    purchase.Source,
                    purchase.BundleKey,
                    purchase.Title,
                    purchase.AcquiredAt,
                    game.ItemKey,
                    game.Title,
                    game.GameId,
                    game.Platform,
                    game.UserLibraryId,
                    game.UserLibraryId is { } libraryId ? libraryTitles.GetValueOrDefault(libraryId) : null);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<BundlePurchaseCandidate>> FindCandidatesAsync(
        int userId,
        string? query,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return [];
        }

        var normalized = GameTitleNormalizer.Normalize(term);
        if (normalized.Length == 0)
        {
            return [];
        }

        var boundedLimit = Math.Clamp(limit ?? 10, 1, 20);
        var games = await repository.Get<Game>()
            .Where(game => game.NormalizedTitle.Contains(normalized))
            .OrderBy(game => game.Title)
            .ThenBy(game => game.GameId)
            .Select(game => new
            {
                game.GameId,
                game.Title,
                game.ReleaseYear,
                game.ImageUrl
            })
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);
        if (games.Count == 0)
        {
            return [];
        }

        var gameIds = games.Select(game => game.GameId).ToList();
        var inLibrary = (await repository.Get<UserLibrary>()
                .Where(row => row.UserId == userId &&
                    row.GameId != null &&
                    gameIds.Contains(row.GameId.Value) &&
                    OwnedStates.Contains(row.State))
                .Select(row => row.GameId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return games
            .Select(game => new BundlePurchaseCandidate(
                game.GameId,
                game.Title,
                game.ReleaseYear,
                game.ImageUrl,
                inLibrary.Contains(game.GameId)))
            .ToList();
    }

    // --- Import -------------------------------------------------------------------------------------

    /// <summary>
    /// Canonical game of every planned item: the ITAD UUID namespace through the shared resolver (which
    /// creates the row when the catalog has never seen it) or the explicit manual link the person chose.
    /// Nothing is written to the library yet.
    /// </summary>
    private async Task<List<(BundlePurchasePlanItem Item, long GameId)>> ResolveIdentitiesAsync(
        BundlePurchasePlan plan,
        CancellationToken cancellationToken)
    {
        var identities = new List<(BundlePurchasePlanItem Item, long GameId)>(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            var gameId = item.CanonicalSource == BundlePurchaseCanonicalSources.Itad
                ? (await identityResolver.ResolveOrCreateGameAsync(
                    new GameIdentityRequest(
                        item.Title,
                        [new GameExternalIdRef(GameExternalIdNamespaces.Itad, item.ItadGameId!)]),
                    cancellationToken)).GameId
                : item.CanonicalGameId
                    ?? throw new ArgumentException("El vínculo canónico manual se perdió antes de importar");

            identities.Add((item, gameId));
        }

        return identities;
    }

    /// <summary>
    /// Writes the owned library row when the game is not already owned on that platform, and returns the
    /// resolved membership in tier order so the purchase and its duplicates read the same way every time.
    /// </summary>
    private async Task<List<ResolvedItem>> ImportAsync(
        BundlePurchase purchase,
        IReadOnlyList<(BundlePurchasePlanItem Item, long GameId)> identities,
        IReadOnlyDictionary<long, string> steamAppIds,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var addedAt = purchase.AcquiredAt is { } acquiredAt
            ? new DateTime(acquiredAt.Year, acquiredAt.Month, acquiredAt.Day, 0, 0, 0, DateTimeKind.Utc)
            : now;

        var resolved = new List<ResolvedItem>(identities.Count);
        foreach (var (item, gameId) in identities)
        {
            var steamAppId = string.Equals(item.Platform, SteamStore, StringComparison.Ordinal)
                ? steamAppIds.GetValueOrDefault(gameId)
                : null;
            var storeGameId = BundlePurchaseRules.StoreGameIdFor(item.Platform, gameId, steamAppId);
            var existingRow = await FindOwnedRowAsync(userId: purchase.UserId, item.Platform, gameId, storeGameId, cancellationToken);
            var outcome = BundlePurchaseRules.OutcomeFor(existingRow is not null);

            long? userLibraryId = existingRow?.UserLibraryId;
            if (existingRow is null)
            {
                var row = new UserLibrary
                {
                    UserId = purchase.UserId,
                    Store = item.Platform,
                    StoreGameId = storeGameId,
                    Title = Truncate(item.Title),
                    State = LibraryStates.Owned,
                    AddedAt = addedAt,
                    ImportedAt = now,
                    GameId = gameId,
                    // The acquisition metadata is one unit and only exists when the shop is known: without
                    // it the row keeps NULLs, which the schema's CHECK accepts and which never invents a
                    // shop this purchase cannot prove.
                    AcquiredStore = purchase.AcquiredStore,
                    AcquisitionType = purchase.AcquiredStore is null ? null : WishlistAcquisitionTypes.Bundle,
                    AcquiredStoreType = purchase.AcquiredStore is null ? null : purchase.AcquiredStoreType,
                    AcquisitionProvider = purchase.AcquiredStore is null ? null : purchase.AcquisitionProvider,
                    AcquisitionDate = purchase.AcquiredStore is null ? null : purchase.AcquiredAt
                };
                repository.GetTrack<UserLibrary>().Add(row);
                await repository.SaveChangesAsync(cancellationToken);
                userLibraryId = row.UserLibraryId;
            }

            resolved.Add(new ResolvedItem(
                item.ItemKey,
                item.Title,
                item.Type,
                item.ItadGameId,
                gameId,
                item.CanonicalSource,
                item.Platform,
                outcome,
                userLibraryId,
                steamAppIds.GetValueOrDefault(gameId)));
        }

        return resolved;
    }

    /// <summary>
    /// Removes the acquired games from the caller's Steam wishlist and stores the protection exclusions.
    /// Duplicates take part: the game was acquired inside the bundle either way.
    /// </summary>
    private async Task<BundlePurchaseWishlistPlan> RemoveWishlistAsync(
        int userId,
        IReadOnlyCollection<ResolvedItem> acquired,
        IReadOnlyDictionary<long, string> appIds,
        CancellationToken cancellationToken)
    {
        var appIdValues = appIds.Values.Distinct(StringComparer.Ordinal).ToList();
        if (appIdValues.Count == 0)
        {
            return new BundlePurchaseWishlistPlan([], []);
        }

        var wished = await repository.GetTrack<UserLibrary>()
            .Where(row => row.UserId == userId &&
                row.Store == SteamStore &&
                row.State == LibraryStates.Wished &&
                appIdValues.Contains(row.StoreGameId))
            .ToListAsync(cancellationToken);
        var excluded = (await repository.Get<WishlistExclusion>()
                .Where(exclusion => exclusion.UserId == userId &&
                    exclusion.Store == SteamStore &&
                    appIdValues.Contains(exclusion.StoreGameId))
                .Select(exclusion => exclusion.StoreGameId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var plan = BundlePurchaseRules.PlanWishlist(
            acquired.Select(item => item.GameId).ToList(),
            appIds,
            wished.Select(row => row.StoreGameId).ToHashSet(StringComparer.Ordinal),
            excluded);

        if (plan.Removals.Count > 0)
        {
            repository.GetTrack<UserLibrary>().RemoveRange(
                wished.Where(row => plan.Removals.Contains(row.StoreGameId, StringComparer.Ordinal)).ToList());
        }

        foreach (var appId in plan.NewExclusions)
        {
            repository.GetTrack<WishlistExclusion>().Add(new WishlistExclusion
            {
                UserId = userId,
                Store = SteamStore,
                StoreGameId = appId
            });
        }

        await repository.SaveChangesAsync(cancellationToken);
        return plan;
    }

    // --- Reads --------------------------------------------------------------------------------------

    private async Task<BundlePurchaseSnapshotRow?> LoadSnapshotAsync(
        string? source,
        string? bundleKey,
        CancellationToken cancellationToken)
    {
        var normalizedSource = source?.Trim();
        var normalizedKey = bundleKey?.Trim();
        if (string.IsNullOrEmpty(normalizedSource) || string.IsNullOrEmpty(normalizedKey))
        {
            throw new ArgumentException("El bundle necesita su fuente y su clave", nameof(source));
        }

        return await repository.Get<ExternalBundle>()
            .Where(bundle => bundle.Source == normalizedSource && bundle.BundleKey == normalizedKey)
            .Select(bundle => new BundlePurchaseSnapshotRow(
                bundle.Source,
                bundle.BundleKey,
                bundle.Title,
                bundle.ShopId,
                bundle.ShopName,
                bundle.PageUrl,
                bundle.DealUrl,
                bundle.Details,
                bundle.PublishedAt,
                bundle.ExpiresAt,
                bundle.ObservedAt,
                bundle.TiersJson))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The canonical game of every ITAD UUID the snapshot already knows. A missing uuid is not an error:
    /// the confirm creates that canonical row through the resolver, exactly like the first price refresh.
    /// </summary>
    private async Task<Dictionary<string, long>> LoadItadCanonicalIdsAsync(
        BundlePurchaseSnapshotRow snapshot,
        CancellationToken cancellationToken)
    {
        var ids = BundlePurchaseRules.ParseTiers(snapshot.TiersJson)
            .SelectMany(tier => tier.Games ?? [])
            .Select(game => game.ItadGameId?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var rows = await repository.Get<GameExternalId>()
            .Where(external => external.NamespaceName == GameExternalIdNamespaces.Itad &&
                ids.Contains(external.ExternalId))
            .Select(external => new { external.ExternalId, external.GameId })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ExternalId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().GameId, StringComparer.Ordinal);
    }

    private async Task<List<BundlePurchaseOwnedPair>> LoadOwnedRowsAsync(
        int userId,
        string? platform,
        CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(platform) ? null : StoreKeys.Normalize(platform);
        if (normalized is null)
        {
            return [];
        }

        return await repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId &&
                row.Store == normalized &&
                OwnedStates.Contains(row.State) &&
                row.GameId != null)
            .OrderBy(row => row.UserLibraryId)
            .Select(row => new BundlePurchaseOwnedPair(row.Store, row.GameId!.Value, row.UserLibraryId))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The purchase of this bundle already stored for the user, matched by source family: an
    /// <c>itad</c> confirm and an <c>itad_global</c> confirm of the same bundle key are one purchase.
    /// </summary>
    private async Task<BundlePurchase?> FindPurchaseAsync(
        int userId,
        string source,
        string bundleKey,
        CancellationToken cancellationToken)
    {
        var candidates = await repository.Get<BundlePurchase>()
            .Where(purchase => purchase.UserId == userId && purchase.BundleKey == bundleKey)
            .OrderBy(purchase => purchase.BundlePurchaseId)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(purchase =>
            BundlePurchaseRules.HidesBundle(purchase.Source, purchase.BundleKey, source, bundleKey));
    }

    private async Task<HashSet<long>> LoadExistingCanonicalIdsAsync(
        BundlePurchaseRequest request,
        CancellationToken cancellationToken)
    {
        var ids = (request.Items ?? [])
            .Select(item => item?.CanonicalGameId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return (await repository.Get<Game>()
                .Where(game => ids.Contains(game.GameId))
                .Select(game => game.GameId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    private async Task<List<BundlePurchaseGame>> LoadPurchaseGamesAsync(
        long purchaseId,
        CancellationToken cancellationToken) =>
        await repository.Get<BundlePurchaseGame>()
            .Where(game => game.BundlePurchaseId == purchaseId)
            .OrderBy(game => game.BundlePurchaseGameId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The owned or subscription row that already covers this game on the chosen platform, matched by
    /// canonical game or by the synthetic store id the import itself writes. Ordered by row id so a legacy
    /// duplicate always answers the same row.
    /// </summary>
    private async Task<UserLibrary?> FindOwnedRowAsync(
        int userId,
        string platform,
        long canonicalGameId,
        string storeGameId,
        CancellationToken cancellationToken) =>
        await repository.GetTrack<UserLibrary>()
            .Where(row => row.UserId == userId &&
                row.Store == platform &&
                OwnedStates.Contains(row.State) &&
                (row.GameId == canonicalGameId || row.StoreGameId == storeGameId))
            .OrderBy(row => row.UserLibraryId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Steam appid of each canonical game, from the claimed mapping first (a merge repoints it, so it is
    /// the identity truth) and from the Steam snapshot second. No appid is ever derived from a title or
    /// from the bundle key.
    /// </summary>
    private async Task<Dictionary<long, string>> LoadSteamAppIdsAsync(
        IReadOnlyList<long> gameIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string>();
        if (gameIds.Count == 0)
        {
            return result;
        }

        var mappings = await repository.Get<GameExternalId>()
            .Where(external => external.NamespaceName == GameExternalIdNamespaces.Steam &&
                gameIds.Contains(external.GameId))
            .Select(external => new { external.GameId, external.ExternalId })
            .ToListAsync(cancellationToken);
        foreach (var mapping in mappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ExternalId))
            .OrderBy(mapping => mapping.ExternalId, StringComparer.Ordinal))
        {
            result.TryAdd(mapping.GameId, mapping.ExternalId.Trim());
        }

        var unmapped = gameIds.Where(id => !result.ContainsKey(id)).ToList();
        if (unmapped.Count == 0)
        {
            return result;
        }

        var snapshots = await repository.Get<SteamGame>()
            .Where(game => game.GameId != null && unmapped.Contains(game.GameId!.Value))
            .Select(game => new { GameId = game.GameId!.Value, game.AppId, game.Region })
            .ToListAsync(cancellationToken);
        foreach (var snapshot in snapshots
            .OrderBy(snapshot => string.Equals(snapshot.Region, "mx", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(snapshot => snapshot.AppId))
        {
            result.TryAdd(snapshot.GameId, snapshot.AppId.ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }

    private async Task<Dictionary<long, string>> LoadLibraryTitlesAsync(
        IReadOnlyList<long> libraryIds,
        int userId,
        CancellationToken cancellationToken)
    {
        if (libraryIds.Count == 0)
        {
            return [];
        }

        return (await repository.Get<UserLibrary>()
                .Where(row => row.UserId == userId && libraryIds.Contains(row.UserLibraryId))
                .Select(row => new { row.UserLibraryId, row.Title })
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.UserLibraryId)
            .ToDictionary(group => group.Key, group => group.First().Title);
    }

    // --- Results ------------------------------------------------------------------------------------

    private async Task<BundlePurchaseResult> BuildResultAsync(
        BundlePurchase purchase,
        IReadOnlyList<BundlePurchaseGame> games,
        bool created,
        bool alreadyReported,
        int? wishlistRemovedCount,
        CancellationToken cancellationToken)
    {
        var duplicates = games
            .Where(game => game.Outcome == BundlePurchaseOutcomes.Duplicate)
            .ToList();
        var libraryTitles = await LoadLibraryTitlesAsync(
            duplicates.Where(game => game.UserLibraryId is not null)
                .Select(game => game.UserLibraryId!.Value)
                .Distinct()
                .ToList(),
            purchase.UserId,
            cancellationToken);

        var results = games
            .Select(game => new BundlePurchaseGameResult(
                game.ItemKey,
                game.Title,
                game.GameId,
                game.Platform,
                game.Outcome,
                game.UserLibraryId,
                game.CanonicalSource,
                game.WishlistRemoved))
            .ToList();

        return new BundlePurchaseResult(
            purchase.BundlePurchaseId,
            created,
            alreadyReported,
            purchase.Source,
            purchase.BundleKey,
            purchase.Title,
            purchase.TierKey,
            purchase.Platform,
            purchase.SnapshotHash,
            purchase.SelectedCount,
            results.Count(result => result.Outcome == BundlePurchaseOutcomes.Imported),
            duplicates.Count,
            wishlistRemovedCount ?? games.Count(game => game.WishlistRemoved),
            results,
            duplicates
                .Select(game => new BundlePurchaseDuplicateEntry(
                    purchase.BundlePurchaseId,
                    purchase.Source,
                    purchase.BundleKey,
                    purchase.Title,
                    purchase.AcquiredAt,
                    game.ItemKey,
                    game.Title,
                    game.GameId,
                    game.Platform,
                    game.UserLibraryId,
                    game.UserLibraryId is { } libraryId ? libraryTitles.GetValueOrDefault(libraryId) : null))
                .ToList());
    }

    private static BundlePurchaseRecord ToRecord(
        BundlePurchase purchase,
        IReadOnlyList<BundlePurchaseGame> games)
    {
        var results = games
            .Select(game => new BundlePurchaseGameResult(
                game.ItemKey,
                game.Title,
                game.GameId,
                game.Platform,
                game.Outcome,
                game.UserLibraryId,
                game.CanonicalSource,
                game.WishlistRemoved))
            .ToList();

        return new BundlePurchaseRecord(
            purchase.BundlePurchaseId,
            purchase.Source,
            purchase.BundleKey,
            purchase.Title,
            purchase.ShopId,
            purchase.ShopName,
            purchase.PageUrl,
            purchase.DealUrl,
            purchase.PublishedAt,
            purchase.ExpiresAt,
            purchase.ObservedAt,
            purchase.TierKey,
            purchase.TierIndex,
            purchase.TierPriceMinor,
            purchase.TierCurrency,
            purchase.TierAddon,
            purchase.TierItemsComplete,
            purchase.TierSelectionCount,
            purchase.SelectionMode,
            purchase.SelectedCount,
            purchase.Platform,
            purchase.AcquiredAt,
            purchase.AcquiredStore,
            purchase.AcquiredStoreType,
            purchase.AcquisitionProvider,
            purchase.SnapshotHash,
            purchase.Created ?? purchase.ObservedAt,
            results.Count(result => result.Outcome == BundlePurchaseOutcomes.Imported),
            results.Count(result => result.Outcome == BundlePurchaseOutcomes.Duplicate),
            results);
    }

    private static string Truncate(string? title)
    {
        var text = (title ?? string.Empty).Trim();
        return text.Length <= BundlePurchaseRules.MaxTitleLength
            ? text
            : text[..BundlePurchaseRules.MaxTitleLength];
    }

    /// <summary>One planned item after identity resolution and the duplicate decision.</summary>
    private sealed record ResolvedItem(
        string ItemKey,
        string Title,
        string? Type,
        string? ItadGameId,
        long GameId,
        string CanonicalSource,
        string Platform,
        string Outcome,
        long? UserLibraryId,
        string? SteamAppId);
}
