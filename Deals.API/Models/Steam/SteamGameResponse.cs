namespace Deals.API.Models.Steam;

/// <summary>
/// Provider-neutral offer snapshot exposed to clients. Includes the original price, the derived MXN
/// value and the FX metadata used for it. Never exposes entity type names or navigation state.
/// </summary>
public sealed record SteamGameOfferResponse(
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
    int? HistoryLowAllMinor,
    string? HistoryLowCurrency);

public sealed record SteamGameResponse(
    int AppId,
    string Name,
    string? Type,
    string? ImageUrl,
    bool IsFree,
    string? Currency,
    int? InitialPriceMinor,
    int? CurrentPriceMinor,
    int? DiscountPercent,
    int? LowestPriceMinor,
    DateTime? LowestPriceAt,
    string Region,
    DateTime ObservedAt,
    IReadOnlyList<SteamGameOfferResponse> Offers,
    DateTime? OffersRefreshedAt,
    bool OffersStale,
    DateTime? GgDealsRefreshedAt,
    bool GgDealsStale);
