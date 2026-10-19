namespace Deals.BusinessLogic.Services;

/// <summary>
/// The one reader for IsThereAnyDeal's <c>Retry-After</c>, shared by both ITAD clients.
///
/// The delay used to be applied verbatim. A provider that answers <c>Retry-After: 86400</c> therefore
/// parked an interactive request - and the per-app gate it holds - for a day, and a sweep for as long as
/// its job budget let it sit there. The provider decides when it wants to be asked again; it does not get
/// to decide how long this process waits. The wait is capped by the budget of the request it is retrying,
/// so one retry can never outlast the call it belongs to by more than that budget.
///
/// gg.deals already caps the equivalent header for its own client (<c>GgDealsClient.MaxRetryWaitSeconds</c>);
/// this is that rule for ITAD, in one place, so the two ITAD clients cannot drift apart again.
///
/// Tied to <c>checks/GlobalDeals.Checks</c> ("Retry-After is capped by the request budget").
/// </summary>
public static class ItadRetryAfter
{
    /// <summary>
    /// Cap used when the client has no time budget of its own (<see cref="HttpClient.Timeout"/> is
    /// infinite): the wait must be bounded even then.
    /// </summary>
    public static readonly TimeSpan StandaloneCap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The provider's requested wait, capped at <paramref name="requestBudget"/>. An absent header, or a
    /// date already in the past, returns <see langword="null"/>: no wait was asked for.
    /// </summary>
    public static TimeSpan? Read(HttpResponseMessage response, TimeSpan requestBudget)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = null;

        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is { } date && date - DateTimeOffset.UtcNow is { Ticks: > 0 } requested)
        {
            delay = requested;
        }

        if (delay is null)
        {
            return null;
        }

        var cap = requestBudget > TimeSpan.Zero ? requestBudget : StandaloneCap;
        return delay.Value > cap ? cap : delay.Value;
    }
}
