namespace Deals.API.Services.Notifications;

/// <summary>
/// Bounded exponential backoff for the outbox retry. Pure so the boundary is checkable.
/// <paramref name="attempts"/> is the count after the failed attempt (1 for the first failure).
/// </summary>
public static class NtfyRetrySchedule
{
    // Beyond 2^20 the seconds value overflows nothing meaningful; the max cap binds long before.
    private const int MaxExponent = 20;

    public static TimeSpan NextDelay(int attempts, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        var exponent = Math.Clamp(attempts - 1, 0, MaxExponent);
        var seconds = baseDelay.TotalSeconds * Math.Pow(2d, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, maxDelay.TotalSeconds));
    }
}
