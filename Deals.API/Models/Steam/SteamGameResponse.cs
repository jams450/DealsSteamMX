namespace Deals.API.Models.Steam;

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
    DateTime ObservedAt);
