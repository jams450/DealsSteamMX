namespace Deals.API.Models.Discovery;

/// <summary>
/// One good deal of the global ITAD deals feed. <c>Currency</c> is always the provider's original
/// currency; <c>MxnCurrentPriceMinor</c> is only present when <c>PricingType</c> is
/// <c>regional</c> or <c>fx_estimate</c> (the second is an estimate converted with the persisted
/// rate). There is deliberately no score or rating: <c>GoodDealReason</c> is a code copied from
/// fields the provider actually reported.
/// </summary>
public sealed record GlobalDealItemResponse(
    string DealKey,
    string ItadGameId,
    string GameTitle,
    string? GameSlug,
    string? GameType,
    bool GameMature,
    string ShopId,
    string ShopName,
    string Currency,
    int CurrentPriceMinor,
    int? RegularPriceMinor,
    int? DiscountPercent,
    int? MxnCurrentPriceMinor,
    string PricingType,
    decimal? FxRate,
    DateOnly? FxRateDate,
    string? FxSource,
    int? HistoryLowMinor,
    string? HistoryLowCurrency,
    bool IsAtHistoryLow,
    string? GoodDealReason,
    string? DealUrl,
    DateTime? ExpiresAt,
    DateTime ObservedAt);

/// <summary>
/// One honest page of the feed. <c>Total</c> is how many good deals outside the wishlist exist,
/// <c>Count</c> is how many this page returned, and <c>HasMore</c> whether a next page exists.
/// </summary>
public sealed record GlobalDealCatalogResponse(
    IReadOnlyList<GlobalDealItemResponse> Items,
    int Count,
    int Total,
    int Page,
    int PageSize,
    bool HasMore);
