using System.Net;

namespace Deals.API.Security;

/// <summary>
/// The trust decision for the forwarded-proxy chain, kept as a pure function so the rule can be
/// exercised without a host (see <c>checks/Auth.Checks</c>, "trusted-proxy" checks).
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
    /// <summary>
    /// Resolves the trust decision from configuration. Entries that are not valid IP addresses are
    /// rejected: a typo must fail loudly instead of silently dropping the proxy it was meant to name.
    /// </summary>
    public static TrustedProxySettings Resolve(bool trustForwardedHeaders, IEnumerable<string>? knownProxies)
    {
        var proxies = new List<IPAddress>();

        foreach (var value in knownProxies ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!IPAddress.TryParse(value.Trim(), out var address))
            {
                throw new InvalidOperationException(
                    $"Proxy:KnownProxies entry '{value.Trim()}' is not a valid IP address.");
            }

            proxies.Add(address);
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
