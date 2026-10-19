namespace Deals.API.Extensions;

/// <summary>
/// The command timeout decision for the PostgreSQL context, kept as a pure function so the rule can be
/// asserted without a database (see <c>checks/IdentityLock.Checks</c>).
///
/// Every command used to run with no timeout at all (<c>SetCommandTimeout(0)</c>), so a command waiting on a
/// lock could pin its pooled connection until the other side finished - and forever when the other side was
/// stuck, which is how one hung writer takes the pool down with it.
///
/// Zero is still accepted, deliberately: it is the escape hatch for a workload that legitimately outlives
/// the default, and it is now an explicit choice instead of the only behaviour. Everything else that is not
/// a positive number of seconds fails at startup rather than being coerced into something surprising.
/// </summary>
public static class DatabaseCommandTimeout
{
    /// <summary>
    /// Finite by default: long enough for the slowest legitimate single command this application issues,
    /// short enough that a stuck one gives its connection back.
    /// </summary>
    public const int DefaultSeconds = 60;

    /// <summary>
    /// Returns the configured timeout in seconds, or throws when the value cannot be one.
    /// An absent value resolves to <see cref="DefaultSeconds"/>.
    /// </summary>
    public static int Resolve(int? configuredSeconds)
    {
        if (configuredSeconds is null)
        {
            return DefaultSeconds;
        }

        if (configuredSeconds < 0)
        {
            throw new InvalidOperationException(
                "Database:CommandTimeoutSeconds must be zero (no timeout) or a positive number of seconds.");
        }

        return configuredSeconds.Value;
    }
}
