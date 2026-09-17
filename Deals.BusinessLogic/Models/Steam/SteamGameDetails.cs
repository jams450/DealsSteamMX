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
    IReadOnlyList<string> PlatformNames);

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
    bool OffersStale = false);
