namespace Deals.API.Models.Steam;

/// <summary>
/// Game-card fields for the discovery lists. Mirrors <c>SteamDiscoverItem</c>: identity, artwork,
/// Steam price snapshot and observation date. No offers, no bundles, no user state.
/// </summary>
public sealed record SteamDiscoverResponse(
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
