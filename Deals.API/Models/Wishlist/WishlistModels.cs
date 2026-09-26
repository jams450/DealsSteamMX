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
    IReadOnlyList<string> OwnedStores);

public sealed record WishlistResponse(
    string State,
    DateTime? SyncedAt,
    IReadOnlyList<WishlistItemResponse> Items,
    int MinViableDiscountPercent);

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
