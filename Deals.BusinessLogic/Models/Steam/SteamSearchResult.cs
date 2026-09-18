namespace Deals.BusinessLogic.Models.Steam;

/// <param name="HasDetails">True when the persisted row already holds a Steam detail snapshot (see
/// <c>SteamGameService.HoldsSteamDetails</c>): a search-only row is false.</param>
/// <param name="RefreshedAt">Newest of the ITAD and gg.deals refresh timestamps; null when neither provider ran.</param>
public sealed record SteamSearchResult(
    int AppId,
    string Name,
    string? Type,
    string? ImageUrl,
    bool HasDetails = false,
    DateTime? RefreshedAt = null);
