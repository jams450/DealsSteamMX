using System.Net;
using Microsoft.Extensions.Configuration;

namespace Deals.API.Security;

/// <summary>
/// The trust decision for the forwarded-proxy chain, kept as a pure function over configuration so the
/// rule can be exercised without a host (see <c>checks/Auth.Checks</c>, "trusted-proxy" checks).
///
/// The rule exists because the auth rate limiter partitions by the client address. While no proxy is
/// trusted, <see cref="HttpContext.Connection"/>.RemoteIpAddress is the immediate peer and every request
/// that arrives through the BFF shares one bucket. Processing <c>X-Forwarded-For</c> fixes that only if
/// the chain that sets it is declared: trusted blindly, that header lets any caller choose its own
/// partition and walk around the limiter entirely. So the default is "do not trust", and asking for
/// trust without naming the proxies is a startup failure rather than a silent no-op.
/// </summary>
public sealed record TrustedProxySettings(bool Enabled, IReadOnlyList<IPAddress> KnownProxies)
{
    private const string SectionName = "Proxy";

    /// <summary>
    /// Reads the decision from the <c>Proxy</c> section.
    ///
    /// <c>Proxy:KnownProxies</c> is accepted in both shapes an operator can actually write: a
    /// comma-separated value (<c>Proxy__KnownProxies=10.0.0.5,10.0.0.6</c>) and a list
    /// (<c>Proxy__KnownProxies__0</c>, or a JSON array). The comma form is a single configuration value
    /// with no children, so reading it as a list yields nothing at all - which would refuse a chain that
    /// was in fact declared, and do it as a startup failure. That is the shape this repository documents.
    /// </summary>
    public static TrustedProxySettings FromConfiguration(IConfigurationSection proxySection)
    {
        var declared = proxySection.GetSection("KnownProxies");

        var children = declared.GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        var entries = children.Length > 0
            ? children
            : string.IsNullOrWhiteSpace(declared.Value) ? [] : [declared.Value!];

        return Resolve(proxySection.GetValue("TrustForwardedHeaders", false), entries);
    }

    /// <summary>
    /// Resolves the trust decision from already-read configuration. Entries that are not valid IP
    /// addresses are rejected: a typo must fail loudly instead of silently dropping the proxy it was
    /// meant to name. A single entry may carry a comma-separated list.
    /// </summary>
    public static TrustedProxySettings Resolve(bool trustForwardedHeaders, IEnumerable<string>? knownProxies)
    {
        var proxies = new List<IPAddress>();

        foreach (var entry in knownProxies ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            foreach (var value in entry.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IPAddress.TryParse(value, out var address))
                {
                    throw new InvalidOperationException(
                        $"Proxy:KnownProxies entry '{value}' is not a valid IP address.");
                }

                proxies.Add(address);
            }
        }

        if (!trustForwardedHeaders)
        {
            return new TrustedProxySettings(false, []);
        }

        if (proxies.Count == 0)
        {
            throw new InvalidOperationException(
                "Proxy:TrustForwardedHeaders is true but Proxy:KnownProxies names no proxy. Refusing to trust an " +
                "unconfigured proxy chain: X-Forwarded-For would then let any caller pick its own rate-limit " +
                "partition.");
        }

        return new TrustedProxySettings(true, proxies);
    }
}
