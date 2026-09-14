namespace Deals.BusinessLogic.Models.Steam;

public sealed record SteamSearchResult(int AppId, string Name, string? Type, string? ImageUrl);
