namespace Deals.API.Models.Wishlist;

/// <summary>
/// One wished game, enriched from the persisted <c>steam_games</c> snapshot when available.
/// <see cref="BasePriceMinor"/>/<see cref="BaseCurrency"/> are Steam's undiscounted list price;
/// <see cref="HistoryLowMinor"/>/<see cref="HistoryLowCurrency"/> are ITAD's all-time low;
/// <see cref="BestOfficialMinor"/> and <see cref="BestKeyshopMinor"/> are the cheapest current MXN price
/// of a legitimate shop and of a keyshop respectively (null when there is no converted price).
/// </summary>
public sealed record WishlistItemResponse(
    int AppId,
    string Name,
    string? ImageUrl,
    int? Priority,
    DateTime? AddedAt,
    string? ItadGameId,
    DateTime? RefreshedAt,
    int? BasePriceMinor,
    string? BaseCurrency,
    int? HistoryLowMinor,
    string? HistoryLowCurrency,
    int? BestOfficialMinor,
    int? BestKeyshopMinor);

public sealed record WishlistResponse(
    string State,
    DateTime? SyncedAt,
    IReadOnlyList<WishlistItemResponse> Items);

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
