namespace Deals.API;

public sealed class FxOptions
{
    public const string SectionName = "FX";
    public string BaseUrl { get; set; } = "https://www.banxico.org.mx";
    public string BaseCurrency { get; set; } = "USD";
    public string QuoteCurrency { get; set; } = "MXN";
    public int TimeoutSeconds { get; set; } = 10;
    public string BanxicoToken { get; set; } = string.Empty;
}
