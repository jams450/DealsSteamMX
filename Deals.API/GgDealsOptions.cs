namespace Deals.API;

public sealed class GgDealsOptions
{
    public const string SectionName = "GgDeals";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.gg.deals/v1/";
    public string Region { get; set; } = "us";
    public int TimeoutSeconds { get; set; } = 10;

    // No RefreshAfterDays here on purpose: the offer cache window is shared with ITAD and comes from SteamOffersSettings.
}
