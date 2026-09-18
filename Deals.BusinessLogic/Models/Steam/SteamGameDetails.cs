namespace Deals.BusinessLogic.Models.Steam;

/// <summary>
/// One current shop offer for a game. Original price/currency is the source of truth; the MXN
/// values are derived and null when no conversion applies (see <see cref="PricingType"/>).
/// </summary>
public sealed record SteamGameOffer(
    string Source,
    string OfferKey,
    string? ShopId,
    string ShopName,
    string Classification,
    string OriginalCurrency,
    int? OriginalRegularPriceMinor,
    int? OriginalCurrentPriceMinor,
    int? MxnRegularPriceMinor,
    int? MxnCurrentPriceMinor,
    decimal? FxRate,
    DateOnly? FxRateDate,
    string? FxSource,
    string PricingType,
    int? DiscountPercent,
    string? DealUrl,
    DateTime ObservedAt,
    IReadOnlyList<string> DrmNames,
    IReadOnlyList<string> PlatformNames,
    int? HistoryLowAllMinor = null,
    string? HistoryLowCurrency = null);

/// <summary>
/// One item listed in a bundle tier, as the display contract expects it: title plus optional type. The
/// provider game id is deliberately not persisted nor exposed.
/// </summary>
public sealed record SteamGameBundleTierGame(string Title, string? Type);

/// <summary>
/// One tier of a bundle: its own price and currency (null when the provider reports none), the addon flag
/// and the items it lists. The price is displayed as published; no saving is ever computed from it.
/// </summary>
public sealed record SteamGameBundleTier(
    int? PriceMinor,
    string? Currency,
    bool Addon,
    IReadOnlyList<SteamGameBundleTierGame> Games);

/// <summary>
/// One external bundle the game appears in. Purely informational: it is not comparable to an offer and
/// never takes part in the price comparison, but it does show the provider prices per tier as reported.
/// </summary>
public sealed record SteamGameBundle(
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
    IReadOnlyList<SteamGameBundleTier> Tiers);

public sealed record SteamGameDetails(
    int AppId,
    string Name,
    string? Type,
    bool IsFree,
    string? Currency,
    int? InitialPriceMinor,
    int? CurrentPriceMinor,
    int? DiscountPercent,
    string Region,
    DateTime ObservedAt,
    string? ImageUrl = null,
    int? LowestPriceMinor = null,
    DateTime? LowestPriceAt = null,
    IReadOnlyList<SteamGameOffer>? Offers = null,
    DateTime? OffersRefreshedAt = null,
    bool OffersStale = false,
    DateTime? GgDealsRefreshedAt = null,
    bool GgDealsStale = false,
    IReadOnlyList<SteamGameBundle>? Bundles = null,
    DateTime? BundlesRefreshedAt = null,
    bool BundlesStale = false);
