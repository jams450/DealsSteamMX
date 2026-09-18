namespace Deals.BusinessLogic.Models.Steam;

using System.Text.Json.Serialization;

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
/// One item listed in a bundle tier, as the display contract expects it: title, optional type and the
/// current ITAD price of the item itself. The provider game id is deliberately not persisted nor exposed,
/// but the item price is what makes the tier comparison honest.
/// </summary>
public sealed record SteamGameBundleTierGame(
    string Title,
    string? Type,
    int? PriceMinor,
    string? PriceCurrency);

/// <summary>
/// One tier of a bundle: its own price, the items it lists and the honest, same-provider comparison
/// derived at read time. The comparison (Status/Reason/individual total/savings) is never persisted: it
/// is recomputed from the persisted tier snapshot, so it cannot become stale truth.
/// </summary>
public sealed record SteamGameBundleTier(
    int? PriceMinor,
    string? Currency,
    bool Addon,
    bool ItemsComplete,
    IReadOnlyList<SteamGameBundleTierGame> Games,
    [property: JsonIgnore] string? Status = null,
    [property: JsonIgnore] string? Reason = null,
    [property: JsonIgnore] int? IndividualTotalMinor = null,
    [property: JsonIgnore] int? SavingsMinor = null,
    [property: JsonIgnore] int? SavingsPercent = null,
    [property: JsonIgnore] decimal? FxRate = null,
    [property: JsonIgnore] DateOnly? FxRateDate = null,
    [property: JsonIgnore] string? FxSource = null,
    [property: JsonIgnore] string? PricingType = null,
    [property: JsonIgnore] int? MxnIndividualTotalMinor = null,
    [property: JsonIgnore] int? MxnSavingsMinor = null);

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
