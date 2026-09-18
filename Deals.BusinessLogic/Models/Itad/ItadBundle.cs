namespace Deals.BusinessLogic.Models.Itad;

/// <summary>
/// One item listed inside a bundle tier. <see cref="Id"/> is the ITAD game id and exists only to
/// attribute the bundle to the queried game and to resolve its current price; it is never persisted nor
/// exposed. <see cref="CurrentPriceMinor"/> is the lowest current ITAD deal price for the item, with its
/// normalized currency; null when ITAD reports no comparable deal.
/// </summary>
public sealed record ItadBundleItem(
    string Id,
    string Title,
    string? Type,
    int? CurrentPriceMinor,
    string? PriceCurrency);

/// <summary>
/// One tier of a bundle: its own price (<see langword="null"/> when the provider reports none), the addon
/// flag and the items it lists. <see cref="ItemsComplete"/> is false when the provider listed more items
/// than the safety cap, which makes the tier ineligible for any saving comparison.
/// </summary>
public sealed record ItadBundleTier(
    int? PriceMinor,
    string? Currency,
    bool Addon,
    bool ItemsComplete,
    IReadOnlyList<ItadBundleItem> Games);

/// <summary>
/// One active external bundle as returned by <c>games/overview/v2</c> under <c>bundles[]</c>. Display
/// data only: a bundle is not an offer and never takes part in the price comparison. Every field is
/// sanitized by the client and the raw provider payload is never carried through.
/// </summary>
public sealed record ItadBundle(
    string BundleKey,
    string Title,
    string? PageUrl,
    string? ShopId,
    string? ShopName,
    string? Details,
    DateTime? PublishedAt,
    DateTime? ExpiresAt,
    string Url,
    IReadOnlyList<ItadBundleTier> Tiers,
    IReadOnlySet<string> MatchedItadIds,
    bool AttributionComplete);
