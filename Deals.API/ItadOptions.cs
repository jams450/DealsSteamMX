namespace Deals.API;

public sealed class ItadOptions
{
    public const string SectionName = "ITAD";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.isthereanydeal.com/";
    public string Country { get; set; } = "MX";
    public int RefreshAfterDays { get; set; } = 7;
    public int TimeoutSeconds { get; set; } = 10;
    public string OfficialShopIds { get; set; } = "61,35,16,62,48,6,36,20,50,64";
}
