namespace Deals.API;

public sealed class SteamOptions
{
    public const string SectionName = "Steam";
    public string StoreBaseUrl { get; set; } = "https://store.steampowered.com/api/";
    public int TimeoutSeconds { get; set; } = 10;
}
