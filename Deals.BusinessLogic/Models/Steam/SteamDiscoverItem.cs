namespace Deals.BusinessLogic.Models.Steam;

/// <summary>
/// Game-card fields for the discovery lists. A read-only projection over persisted <c>steam_games</c>
/// rows (region MX with a current price): no live store call, no new snapshot, no schema change.
/// </summary>
public sealed record SteamDiscoverItem(
    int AppId,
    string Name,
    string? Type,
    string? ImageUrl,
    string? Currency,
    int? InitialPriceMinor,
    int? CurrentPriceMinor,
    int? LowestPriceMinor,
    DateTime ObservedAt,
    int? BestCurrentPriceMinor,
    string? BestPriceCurrency,
    string? BestPriceSource,
    string? BestPriceLabel,
    string? BestPriceClassification,
    string? BestPricePricingType,
    int? BestDiscountPercent,
    bool UsesSteamFallback);
