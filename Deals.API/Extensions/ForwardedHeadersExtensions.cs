using Deals.API.Security;
using Microsoft.AspNetCore.HttpOverrides;

namespace Deals.API.Extensions;

public static class ForwardedHeadersExtensions
{
    private const string SectionName = "Proxy";

    /// <summary>
    /// Registers <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> processing only when the proxy chain is
    /// declared. With the default configuration nothing is registered, so the request address stays the
    /// immediate peer: a caller cannot influence the auth rate-limit partition, the login lockout or the
    /// audit trail by sending headers.
    ///
    /// Declaring the chain is what makes the auth limiter fair behind a proxy: set
    /// <c>Proxy:TrustForwardedHeaders</c> to true and name the proxies in <c>Proxy:KnownProxies</c> (the
    /// frontend container or the ingress address). Only exact addresses are supported; the implicit
    /// loopback entry is dropped, and only one hop is trusted, so a chain longer than one proxy is
    /// ignored rather than followed.
    /// </summary>
    public static WebApplication UseApiForwardedHeaders(this WebApplication app)
    {
        var section = app.Configuration.GetSection(SectionName);
        var settings = TrustedProxySettings.Resolve(
            section.GetValue("TrustForwardedHeaders", false),
            section.GetSection("KnownProxies").Get<string[]>());

        if (!settings.Enabled)
        {
            return app;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };

        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();

        foreach (var proxy in settings.KnownProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        app.UseForwardedHeaders(options);
        return app;
    }
}
