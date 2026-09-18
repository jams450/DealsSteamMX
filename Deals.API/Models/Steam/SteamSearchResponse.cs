namespace Deals.API.Models.Steam;

public sealed record SteamSearchResponse(
    int AppId,
    string Name,
    string? Type,
    string? ImageUrl,
    bool HasDetails,
    DateTime? RefreshedAt);
