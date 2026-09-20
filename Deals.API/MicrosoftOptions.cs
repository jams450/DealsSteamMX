namespace Deals.API;

/// <summary>
/// Microsoft Store (Xbox) catalog access. The market, the language and the currency are configuration
/// because they decide the price that comes back: this client never converts, so a wrong market is a
/// silently wrong price.
/// </summary>
public sealed class MicrosoftOptions
{
    public const string SectionName = "Microsoft";

    /// <summary>Catalog host, no path: the client appends <c>v7.0/products/lookup</c>.</summary>
    public string BaseUrl { get; set; } = "https://displaycatalog.mp.microsoft.com/";

    public string Market { get; set; } = "MX";

    public string Languages { get; set; } = "es-mx";

    /// <summary>
    /// Currency a purchasable availability must declare to be accepted. The catalog also answers licence rows
    /// in USD for the same SKU, and reading one of those would be a fabricated price.
    /// </summary>
    public string Currency { get; set; } = "MXN";

    /// <summary>
    /// Sent on every call for the same reason Epic's is: a request with no agent is the shape a bot filter
    /// looks for, and identifying the caller is the honest alternative to looking like a browser.
    /// </summary>
    public string UserAgent { get; set; } = "DealsExt/1.0 (+https://amitzi.xyz)";

    public int TimeoutSeconds { get; set; } = 10;
}
