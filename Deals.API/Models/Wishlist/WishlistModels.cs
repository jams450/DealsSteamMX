using System.ComponentModel.DataAnnotations;

namespace Deals.API.Models.Wishlist;

/// <summary>
/// One wished game, enriched from the persisted <c>steam_games</c> snapshot when available.
/// <see cref="BasePriceMinor"/>/<see cref="BaseCurrency"/> are Steam's undiscounted list price;
/// <see cref="HistoryLowMinor"/>/<see cref="HistoryLowCurrency"/> are ITAD's all-time low;
/// <see cref="BestOfficialMinor"/> and <see cref="BestKeyshopMinor"/> are the cheapest current MXN price
/// of a legitimate shop and of a keyshop respectively (null when there is no converted price).
/// <see cref="BestOfficialMinor"/> spans every source, the direct Steam snapshot included: it is the
/// minimum among ITAD, Epic, Microsoft and Steam.
/// <see cref="BestOfficialObservedAt"/>/<see cref="BestKeyshopObservedAt"/> are the observation date of the very
/// offer whose price and shop metadata those columns report, so the ≤72h/+72h label describes a price and a
/// date of the same source. They are not provider consultation stamps (<c>*SyncedAt</c>): a provider can be
/// consulted minutes ago and still quote a price seen days ago, and only the latter describes the figure on
/// screen. Null means no comparable price, never "now".
/// The five <c>*SyncedAt</c> fields are the per-provider refresh stamps the snapshot already carries, one
/// per source, so the row can say which store is in sync and when it last was. They are not derived from
/// each other: a provider that failed keeps its old stamp while the others advance.
/// </summary>
public sealed record WishlistItemResponse(
    int AppId,
    string Name,
    string? ImageUrl,
    int? Priority,
    DateTime? AddedAt,
    string? ItadGameId,
    DateTime? RefreshedAt,
    DateTime? SteamSyncedAt,
    DateTime? ItadSyncedAt,
    DateTime? GgDealsSyncedAt,
    DateTime? EpicSyncedAt,
    DateTime? MicrosoftSyncedAt,
    int? BasePriceMinor,
    string? BaseCurrency,
    int? HistoryLowMinor,
    string? HistoryLowCurrency,
    int? BestOfficialMinor,
    int? BestKeyshopMinor,
    IReadOnlyList<string> OwnedStores,
    IReadOnlyList<WishlistCategorySummary>? Categories = null,
    string? BestOfficialSource = null,
    string? BestOfficialLabel = null,
    string? BestOfficialClassification = null,
    string? BestOfficialPricingType = null,
    string? BestKeyshopSource = null,
    string? BestKeyshopLabel = null,
    string? BestKeyshopClassification = null,
    string? BestKeyshopPricingType = null,
    DateTime? BestOfficialObservedAt = null,
    DateTime? BestKeyshopObservedAt = null);

public sealed record WishlistCategorySummary(long Id, string Name, int ItemCount);

/// <summary>Validated, bounded query parameters for the server-paginated wishlist.</summary>
public sealed class WishlistQueryRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public string? Search { get; init; }
    public int? MinPrice { get; init; }
    public int? MaxPrice { get; init; }
    public string? Owned { get; init; }
    public string? Subscription { get; init; }
    public IReadOnlyList<long>? CategoryIds { get; init; }
    public bool Uncategorized { get; init; }
    public string? Sort { get; init; }
    public string? Direction { get; init; }
}

public sealed record WishlistResponse(
    string State,
    DateTime? SyncedAt,
    IReadOnlyList<WishlistItemResponse> Items,
    int MinViableDiscountPercent,
    IReadOnlyList<WishlistCategorySummary> Categories,
    int Page = 1,
    int PageSize = 50,
    int TotalItems = 0,
    int TotalPages = 0);

public sealed record WishlistCategoryRequest(string? Name);
public sealed record WishlistCategoryRenameRequest(string? Name);
public sealed record WishlistCategoryAssignmentRequest(IReadOnlyList<int>? AppIds);
public sealed record WishlistItemCategoriesRequest(IReadOnlyList<long>? CategoryIds);
public sealed record WishlistCategoryBatchResponse(int Requested, int Matched, int Changed, int Ignored);

/// <summary>
/// Request of the wishlist → library acquisition (specs S1/S2/S6). The platform is the library the row belongs
/// to; <c>acquiredStore</c> is where the copy came from and is a separate fact. <c>acquiredAt</c> is a
/// date-only value that may be null — the feature never invents a purchase date. <c>removeFromWishlist</c>
/// removes the wished row locally and remembers the removal so the next Steam sync does not undo it; Steam
/// itself is never written to.
///
/// <para>
/// <c>removeFromWishlist</c> is nullable here only so an omitted field is detectable: the contract makes it
/// required, and a missing one is a 400 instead of a silent "false" that leaves the wishlist untouched.
/// </para>
///
/// <para>
/// <c>acquiredStoreType</c> (<c>official | keyshop | unknown</c>) classifies the shop and
/// <c>acquisitionProvider</c> (<c>itad | ggdeals | direct | other</c>) says where the offer was found. Both
/// are <b>optional</b> and additive: absent or null keeps the previous payload working and stores NULL, and
/// neither is ever inferred from a price. Only a non-null value outside its vocabulary is a 400.
/// </para>
/// </summary>
public sealed record WishlistAcquireRequest(
    string? Platform,
    string? AcquiredStore,
    string? AcquisitionType,
    DateOnly? AcquiredAt,
    bool? RemoveFromWishlist,
    string? AcquiredStoreType = null,
    string? AcquisitionProvider = null);

/// <summary>
/// Result of one acquisition. <c>outcome</c> is <c>created</c> or <c>already_owned</c>; <c>dismissed</c> says
/// whether the wished row was removed locally, and is true on a repeated submit too.
/// </summary>
public sealed record WishlistAcquireResponse(
    string Outcome,
    long UserLibraryId,
    long GameId,
    bool Dismissed);

/// <summary>
/// Request of the package preview: the appids the user selected in the table. Prices are never accepted
/// from the client; the server reads its own snapshot again.
/// </summary>
public sealed record WishlistPackagePreviewRequest(IReadOnlyList<int>? AppIds);

/// <summary>
/// Cost of a set of wished games under two <b>alternative</b> scenarios, computed on the server from the
/// same snapshot the table reads.
///
/// <para>
/// The two subtotals are not two halves of a total: <c>official</c> means "every game bought from a
/// legitimate shop" and <c>keyshop</c> means "every game bought from the keyshop aggregate". They never
/// add up and no combined field exists on purpose. A game with no price in a scenario is counted in that
/// scenario's <c>Missing</c> count and listed in <c>MissingAppIds</c>; its absence never becomes a 0.
/// </para>
///
/// <para>
/// <c>SelectedCount</c> is the size of the request after dropping appids that are not in the caller's
/// wishlist: the preview never confirms that a foreign appid exists. <c>PricedAt</c> is when the prices
/// were read, so the UI can say how fresh the figures are.
/// </para>
/// </summary>
public sealed record WishlistPackagePreviewResponse(
    int RequestedCount,
    int SelectedCount,
    IReadOnlyList<int> UnmatchedAppIds,
    long? OfficialSubtotalMinor,
    int OfficialQuoted,
    int OfficialMissing,
    IReadOnlyList<int> OfficialMissingAppIds,
    long? KeyshopSubtotalMinor,
    int KeyshopQuoted,
    int KeyshopMissing,
    IReadOnlyList<int> KeyshopMissingAppIds,
    string Currency,
    DateTime PricedAt);

/// <summary>
/// Per-user wishlist scoring preference. Percent is 0..95; null means the body omitted it.
/// </summary>
public sealed record WishlistPreferencesRequest(
    [property: Range(0, 95)] int? MinViableDiscountPercent);

public sealed record WishlistPreferencesResponse(
    int MinViableDiscountPercent);

/// <summary>
/// Result of a manual list sync. Refreshed/Failed are always 0: the price refresh is the background
/// job's job and never runs inside a request. FetchedFromSteam/FetchFailed count the list entries that
/// had no local row and were pulled from Steam during this sync.
/// </summary>
public sealed record WishlistSyncResponse(
    string State,
    int ItemCount,
    int Added,
    int Updated,
    int Removed,
    int Refreshed,
    int Failed,
    DateTime? SyncedAt,
    int FetchedFromSteam,
    int FetchFailed);
