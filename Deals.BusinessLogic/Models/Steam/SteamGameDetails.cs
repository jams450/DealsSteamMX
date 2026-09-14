namespace Deals.BusinessLogic.Models.Steam;

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
    DateTime ObservedAt);
