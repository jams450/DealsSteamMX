namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// One imported or duplicate game of a stored purchase. <see cref="UserLibraryId"/> is the owned row that
/// already covers the game (a duplicate) or the row this purchase created (an import); it is null only for
/// a duplicate whose original row vanished, which the endpoint reports instead of hiding.
/// </summary>
public sealed record BundlePurchaseGameResult(
    string ItemKey,
    string Title,
    long GameId,
    string Platform,
    string Outcome,
    long? UserLibraryId,
    string CanonicalSource,
    bool WishlistRemoved);

/// <summary>
/// One entry of the caller's duplicate inventory: a game the purchase did not import because it was
/// already owned on that platform, with the bundle it came in and the row that already held it.
/// </summary>
public sealed record BundlePurchaseDuplicateEntry(
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
/// Stored purchase as the inventory endpoint reads it. <see cref="SnapshotHash"/> is the hash of the
/// provider snapshot frozen at confirm time, and <see cref="TierKey"/> identifies the purchased tier.
/// </summary>
public sealed record BundlePurchaseRecord(
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
    IReadOnlyList<BundlePurchaseGameResult> Games);

/// <summary>
/// Answer of one confirm. <see cref="Created"/> is false when the same (user, source, bundle) was already
/// recorded: the replay writes nothing and returns the stored result, so a double submit cannot import
/// twice or record a second duplicate.
/// </summary>
public sealed record BundlePurchaseResult(
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
    IReadOnlyList<BundlePurchaseGameResult> Games,
    IReadOnlyList<BundlePurchaseDuplicateEntry> Duplicates);

/// <summary>
/// One existing canonical game offered as a manual link for an item the provider identified poorly. A
/// suggestion only: nothing here asserts identity and nothing is created or selected by the server.
/// </summary>
public sealed record BundlePurchaseCandidate(
    long GameId,
    string Title,
    int? ReleaseYear,
    string? ImageUrl,
    bool InLibrary);
