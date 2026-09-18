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

/// <summary>
/// Item listed in a bundle tier. Mirrors the frontend contract exactly: title plus optional type.
/// </summary>
public sealed record SteamGameBundleTierGameResponse(string Title, string? Type);

/// <summary>
/// One tier of a bundle as published by the provider. <paramref name="PriceMinor"/> and
/// <paramref name="Currency"/> are null when no price is reported; <paramref name="Addon"/> marks a tier
/// that is an add-on. No saving is derived from these values.
/// </summary>
public sealed record SteamGameBundleTierResponse(
    int? PriceMinor,
    string? Currency,
    bool Addon,
    IReadOnlyList<SteamGameBundleTierGameResponse> Games);

/// <summary>
/// External bundle the game appears in. Display-only metadata: it never feeds the offer comparison.
/// <see cref="PageUrl"/> and <see cref="DealUrl"/> are provider URLs kept verbatim (the latter carries
/// the affiliate tag); <see cref="Tiers"/> is the sanitized tier list. Expired bundles are not returned.
/// </summary>
public sealed record SteamGameBundleResponse(
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
    IReadOnlyList<SteamGameBundleTierResponse> Tiers);

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
    bool GgDealsStale,
    IReadOnlyList<SteamGameBundleResponse> Bundles,
    DateTime? BundlesRefreshedAt,
    bool BundlesStale);
