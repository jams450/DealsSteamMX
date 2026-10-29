using Deals.BusinessLogic.Models.Bundles;

namespace Deals.API.Models.Bundles;

/// <summary>
/// One selected tier item of a confirm. <c>ItemKey</c> is the stable key the preview returned;
/// <c>Platform</c> is the optional per-game override; <c>CanonicalGameId</c> is the explicit link the
/// person chose for an item the provider did not identify. Every field is optional because the body is
/// untrusted and the server revalidates it.
/// </summary>
public sealed record BundlePurchaseItemRequest(
    string? ItemKey,
    string? Platform,
    long? CanonicalGameId);

/// <summary>
/// Confirm payload. <c>SnapshotHash</c> must be the value the preview returned; <c>TierKey</c> identifies
/// the purchased tier. <c>Items</c> may be omitted for a fixed tier only when every eligible game already
/// has reliable provider identity; otherwise the whole membership must be sent with its manual links.
/// </summary>
public sealed record BundlePurchaseConfirmRequest(
    string? Source,
    string? BundleKey,
    string? TierKey,
    string? Platform,
    string? SnapshotHash,
    DateOnly? AcquiredAt,
    string? AcquiredStore,
    string? AcquiredStoreType,
    string? AcquisitionProvider,
    IReadOnlyList<BundlePurchaseItemRequest>? Items)
{
    public BundlePurchaseRequest ToBusinessRequest() =>
        new(
            Source,
            BundleKey,
            TierKey,
            Platform,
            SnapshotHash,
            AcquiredAt,
            AcquiredStore,
            AcquiredStoreType,
            AcquisitionProvider,
            Items?
                .Where(item => item is not null)
                .Select(item => new BundlePurchaseItemSelection(item.ItemKey, item.Platform, item.CanonicalGameId))
                .ToList());
}

/// <summary>
/// One tier item as the preview returns it. <c>ItemKey</c> is the value the confirm must repeat;
/// <c>RequiresManualLink</c> marks an item without a provider id, whose <c>CanonicalGameId</c> is then null
/// until the person picks one in the candidate search.
/// </summary>
public sealed record BundlePurchasePreviewItemResponse(
    string ItemKey,
    string Title,
    string? Type,
    int? PriceMinor,
    string? Currency,
    string? ItadGameId,
    string? Url,
    bool Eligible,
    long? CanonicalGameId,
    string? CanonicalSource,
    bool RequiresManualLink,
    bool? Owned,
    long? OwnedUserLibraryId,
    string? OwnedPlatform);

/// <summary>
/// One preview tier. <c>Importable</c> is false when the tier cannot be confirmed, and
/// <c>BlockedReason</c> says why (<c>items_incomplete</c>, <c>addon_tier</c>, <c>no_eligible_games</c>,
/// <c>invalid_selection_count</c>). <c>RequiredSelectionCount</c> is the size a valid confirm must have.
/// </summary>
public sealed record BundlePurchasePreviewTierResponse(
    string TierKey,
    int TierIndex,
    int? PriceMinor,
    string? Currency,
    bool Addon,
    bool ItemsComplete,
    int? SelectionCount,
    string SelectionMode,
    int RequiredSelectionCount,
    bool Importable,
    string? BlockedReason,
    IReadOnlyList<BundlePurchasePreviewItemResponse> Items);

/// <summary>
/// Read-only preview of one persisted bundle. <c>SnapshotHash</c> is what the confirm sends back, so a
/// rewritten provider snapshot is rejected instead of imported from stale membership.
/// </summary>
public sealed record BundlePurchasePreviewResponse(
    string Source,
    string BundleKey,
    string Title,
    string? ShopId,
    string? ShopName,
    string? PageUrl,
    string? DealUrl,
    string? Details,
    DateTime? PublishedAt,
    DateTime? ExpiresAt,
    DateTime ObservedAt,
    string SnapshotHash,
    bool Purchased,
    long? PurchaseId,
    IReadOnlyList<BundlePurchasePreviewTierResponse> Tiers);

/// <summary>One imported or duplicate game of a stored purchase.</summary>
public sealed record BundlePurchaseGameResponse(
    string ItemKey,
    string Title,
    long GameId,
    string Platform,
    string Outcome,
    long? UserLibraryId,
    string CanonicalSource,
    bool WishlistRemoved);

/// <summary>One entry of the caller's duplicate inventory.</summary>
public sealed record BundlePurchaseDuplicateResponse(
    long PurchaseId,
    string Source,
    string BundleKey,
    string BundleTitle,
    DateOnly? AcquiredAt,
    string ItemKey,
    string Title,
    long GameId,
    string Platform,
    long? UserLibraryId,
    string? ExistingTitle);

/// <summary>
/// Answer of one confirm. <c>Created</c> is false on a repeat: nothing was written and the stored result is
/// returned, so a double submit cannot import twice or record a second duplicate.
/// </summary>
public sealed record BundlePurchaseConfirmResponse(
    long PurchaseId,
    bool Created,
    bool AlreadyReported,
    string Source,
    string BundleKey,
    string Title,
    string TierKey,
    string Platform,
    string SnapshotHash,
    int SelectedCount,
    int ImportedCount,
    int DuplicateCount,
    int WishlistRemovedCount,
    IReadOnlyList<BundlePurchaseGameResponse> Games,
    IReadOnlyList<BundlePurchaseDuplicateResponse> Duplicates);

/// <summary>One stored purchase with the games it imported or found owned.</summary>
public sealed record BundlePurchaseListResponse(
    long PurchaseId,
    string Source,
    string BundleKey,
    string Title,
    string? ShopId,
    string? ShopName,
    string? PageUrl,
    string? DealUrl,
    DateTime? PublishedAt,
    DateTime? ExpiresAt,
    DateTime ObservedAt,
    string TierKey,
    int TierIndex,
    int? TierPriceMinor,
    string? TierCurrency,
    bool TierAddon,
    bool TierItemsComplete,
    int? TierSelectionCount,
    string SelectionMode,
    int SelectedCount,
    string Platform,
    DateOnly? AcquiredAt,
    string? AcquiredStore,
    string? AcquiredStoreType,
    string? AcquisitionProvider,
    string SnapshotHash,
    DateTime CreatedAt,
    int ImportedCount,
    int DuplicateCount,
    IReadOnlyList<BundlePurchaseGameResponse> Games);

/// <summary>One page-shaped inventory answer (the list is not paginated today, it is bounded by user).</summary>
public sealed record BundlePurchaseListEnvelope(
    IReadOnlyList<BundlePurchaseListResponse> Items,
    int Count);

/// <summary>Duplicate inventory answer.</summary>
public sealed record BundlePurchaseDuplicateEnvelope(
    IReadOnlyList<BundlePurchaseDuplicateResponse> Items,
    int Count);

/// <summary>
/// Read-only canonical candidate for a manual link. <c>InLibrary</c> says the caller already owns it on
/// some platform; it never asserts identity and never selects anything.
/// </summary>
public sealed record BundlePurchaseCandidateResponse(
    long GameId,
    string Title,
    int? ReleaseYear,
    string? ImageUrl,
    bool InLibrary);

/// <summary>Candidate search answer.</summary>
public sealed record BundlePurchaseCandidateEnvelope(
    IReadOnlyList<BundlePurchaseCandidateResponse> Items,
    int Count);
