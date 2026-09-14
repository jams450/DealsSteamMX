namespace Deals.API.Models.Steam;

public sealed record SteamGameResponse(
    int AppId,
    string Name,
    string? Type,
    bool IsFree,
    string? Currency,
    int? InitialPriceMinor,
    int? CurrentPriceMinor,
    int? DiscountPercent,
    string Region,
    DateTime ObservedAt);
