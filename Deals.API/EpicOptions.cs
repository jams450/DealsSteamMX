namespace Deals.API;

public sealed class EpicOptions
{
    public const string SectionName = "Epic";

    /// <summary>Public store host, no path: the client posts the GraphQL operation to <c>/graphql</c>.</summary>
    public string BaseUrl { get; set; } = "https://store.epicgames.com/";

    public string Country { get; set; } = "MX";

    /// <summary>Store locale. Also the segment used in the persisted store link.</summary>
    public string Locale { get; set; } = "es-MX";

    /// <summary>
    /// Required in practice: the store sits behind Cloudflare, which answers 403 to a request with no
    /// User-Agent and to well-known bot agents (<c>curl/...</c>). A descriptive agent passes. Do not replace
    /// it with a browser User-Agent: impersonating a browser is what makes the call fragile when the
    /// protection tightens, and this one is verified to pass.
    /// </summary>
    public string UserAgent { get; set; } = "DealsExt/1.0 (+https://amitzi.xyz)";

    public int TimeoutSeconds { get; set; } = 10;
}
