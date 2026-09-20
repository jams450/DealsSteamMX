namespace Deals.BusinessLogic.Models.Itad;

public sealed record ItadDeal(
    string ShopId,
    string ShopName,
    string Currency,
    int? RegularPriceMinor,
    int? CurrentPriceMinor,
    int? DiscountPercent,
    string? DealUrl,
    DateTime ObservedAt,
    IReadOnlyList<string> DrmNames,
    IReadOnlyList<string> PlatformNames);
