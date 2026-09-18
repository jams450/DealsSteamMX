namespace Deals.API.Models.Steam;

using Deals.API.Models.Reviews;

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
/// Item listed in a bundle tier. Mirrors the frontend contract exactly. <see cref="PriceMinor"/> and
/// <see cref="PriceCurrency"/> are the item's current ITAD price, reported by the same provider as the tier.
/// </summary>
public sealed record SteamGameBundleTierGameResponse(
    string Title,
    string? Type,
    int? PriceMinor,
    string? PriceCurrency);

/// <summary>
/// One tier of a bundle with the honest, same-provider comparison derived at read time. The savings are
/// only present when <see cref="Status"/> is "ok"; otherwise <see cref="Reason"/> carries why. FX/MXN
/// fields are the optional single conversion of that comparison, only for a USD tier with a day rate.
/// </summary>
public sealed record SteamGameBundleTierResponse(
    int? PriceMinor,
    string? Currency,
    bool Addon,
    bool ItemsComplete,
    IReadOnlyList<SteamGameBundleTierGameResponse> Games,
    string? Status,
    string? Reason,
    int? IndividualTotalMinor,
    int? BundlePriceMinor,
    int? SavingsMinor,
    int? SavingsPercent,
    decimal? FxRate,
    DateOnly? FxRateDate,
    string? FxSource,
    string? PricingType,
    int? MxnIndividualTotalMinor,
    int? MxnSavingsMinor);

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

/// <summary>
/// Additive ownership block of the game detail. <see cref="PossibleMatchStores"/> is a read-only
/// normalized-title candidate and never asserts ownership; a subscription is reported only through
/// <see cref="HasGamePass"/>. Store keys reuse the <c>user_library.store</c> vocabulary and never
/// include <c>steam</c> (the page itself).
/// </summary>
public sealed record SteamGameOwnershipResponse(
    string[] OwnedStores,
    bool HasGamePass,
    string[] PossibleMatchStores);

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
    bool BundlesStale,
    SteamGameOwnershipResponse Ownership,
    IReadOnlyList<ReviewResponse> Reviews);
