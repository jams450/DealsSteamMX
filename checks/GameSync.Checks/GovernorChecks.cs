using System.Net;
using System.Net.Http.Headers;
using Deals.BusinessLogic.Services;

internal static class GovernorChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
        static async Task Cancelled(Task task)
        {
            try { await task; throw new Exception("expected cancellation"); }
            catch (OperationCanceledException) { }
        }
        using var epic = new EpicRequestGovernor(30, 600, 0);
        using var ms = new MicrosoftRequestGovernor(30, 600, 0);
        using (var lease = await epic.AcquireAsync(default))
        {
            using var cancel = new CancellationTokenSource(40);
            var waiting = epic.AcquireAsync(cancel.Token).AsTask();
            using var independent = await ms.AcquireAsync(default);
            Check(!waiting.IsCompleted, "same provider serialized, other provider independent");
            await Cancelled(waiting);
        }
        var released = await epic.AcquireAsync(default);
        released.Dispose();
        released.Dispose();
        using (await epic.AcquireAsync(default)) { }
        // Each lease represents one HTTP attempt, including lookup/search/pricing.
        foreach (var limits in new[] { (1, 100, 80, 200), (100, 1, 40, 80), (100, 100, 200, 200) })
        {
            var minDelay = limits.Item1 == 100 && limits.Item2 == 100 ? 80 : 0;
            var windowClock = new Clock();
            using var governor = new EpicRequestGovernor(limits.Item1, limits.Item2, minDelay, windowClock,
                minuteWindow: TimeSpan.FromMilliseconds(limits.Item3), hourWindow: TimeSpan.FromMilliseconds(limits.Item4));
            using (await governor.AcquireAsync(default)) { }
            windowClock.Advance(TimeSpan.FromMilliseconds(79));
            using var cancel = new CancellationTokenSource();
            var blocked = governor.AcquireAsync(cancel.Token).AsTask();
            Check(!blocked.IsCompleted, "minute/hour/min-delay admission blocked before deadline");
            cancel.Cancel();
            await Cancelled(blocked);
            windowClock.Advance(TimeSpan.FromMilliseconds(1));
            using var timeout = new CancellationTokenSource(1000);
            using (await governor.AcquireAsync(timeout.Token)) { } // boundary admitted; cancelled wait released gate
        }
        var clock = new Clock();
        using var cooldown = new MicrosoftRequestGovernor(100, 100, 0, clock);
        foreach (var kind in new[] { "delta", "date", "malformed", "absent", "negative" })
        {
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            var seconds = kind is "delta" or "date" ? 120 : 60;
            if (kind == "delta") response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            if (kind == "date") response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(seconds));
            if (kind == "malformed") response.Headers.TryAddWithoutValidation("Retry-After", "not-a-delay");
            if (kind == "negative") response.Headers.TryAddWithoutValidation("Retry-After", "-1");
            cooldown.ObserveResponse(response);
            using var shorter = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            shorter.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            cooldown.ObserveResponse(shorter); // must not shorten an existing cooldown
            clock.Advance(TimeSpan.FromSeconds(seconds - 1));
            using var cancel = new CancellationTokenSource(10);
            await Cancelled(cooldown.AcquireAsync(cancel.Token).AsTask());
            clock.Advance(TimeSpan.FromSeconds(1));
            using var timeout = new CancellationTokenSource(1000);
            using (await cooldown.AcquireAsync(timeout.Token)) { }
        }
        using var ok = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        ok.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        cooldown.ObserveResponse(ok);
        using var finalTimeout = new CancellationTokenSource(1000);
        using (await cooldown.AcquireAsync(finalTimeout.Token)) { }
        Console.WriteLine("Governor checks passed: serialization/isolation, windows/delay, cancellation, 429 delta/date/fallback, non429.");
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
