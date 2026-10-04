using System.Net;

namespace Deals.BusinessLogic.Services;

/// <summary>Local direct-store safety policy, not a verified external quota. One admission per HTTP attempt.</summary>
public class ProviderRequestGovernor : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Queue<DateTimeOffset> attempts = new();
    private readonly TimeProvider clock;
    private readonly int minuteLimit, hourLimit;
    private readonly TimeSpan minuteWindow, hourWindow, minDelay;
    private DateTimeOffset nextAdmission, cooldownUntil;

    public ProviderRequestGovernor(int requestsPerMinute = 30, int requestsPerHour = 600,
        int minDelayMilliseconds = 1000, TimeProvider? timeProvider = null,
        TimeSpan? minuteWindow = null, TimeSpan? hourWindow = null)
    {
        this.minuteWindow = minuteWindow ?? TimeSpan.FromMinutes(1);
        this.hourWindow = hourWindow ?? TimeSpan.FromHours(1);
        if (requestsPerMinute <= 0 || requestsPerHour <= 0 || minDelayMilliseconds < 0 ||
            this.minuteWindow <= TimeSpan.Zero || this.hourWindow < this.minuteWindow)
            throw new ArgumentOutOfRangeException(nameof(requestsPerMinute));
        minuteLimit = requestsPerMinute;
        hourLimit = requestsPerHour;
        minDelay = TimeSpan.FromMilliseconds(minDelayMilliseconds);
        clock = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan wait;
                lock (sync)
                {
                    var now = clock.GetUtcNow();
                    while (attempts.TryPeek(out var oldest) && oldest + hourWindow <= now) attempts.Dequeue();
                    var due = nextAdmission > cooldownUntil ? nextAdmission : cooldownUntil;
                    var minuteAttempts = attempts.Where(at => at + minuteWindow > now).ToArray();
                    if (minuteAttempts.Length >= minuteLimit)
                        due = Max(due, minuteAttempts[minuteAttempts.Length - minuteLimit] + minuteWindow);
                    if (attempts.Count >= hourLimit)
                        due = Max(due, attempts.ElementAt(attempts.Count - hourLimit) + hourWindow);
                    wait = due - now;
                    if (wait <= TimeSpan.Zero)
                    {
                        attempts.Enqueue(now);
                        nextAdmission = now + minDelay;
                        return new Release(gate);
                    }
                }
                // Chunk only the wait, never the server deadline (Task.Delay has a finite range).
                await Task.Delay(wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait, cancellationToken);
            }
        }
        catch { gate.Release(); throw; }
    }

    public void ObserveResponse(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return;
        lock (sync)
        {
            var now = clock.GetUtcNow();
            var retry = response.Headers.RetryAfter;
            var until = retry?.Delta is { } delta && delta >= TimeSpan.Zero ? now + delta
                : retry?.Date is { } date ? Max(now, date) : now.AddSeconds(60);
            cooldownUntil = Max(cooldownUntil, until);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    public void Dispose() => gate.Dispose();
    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? owned = gate;
        public void Dispose() => Interlocked.Exchange(ref owned, null)?.Release();
    }
}

public sealed class EpicRequestGovernor(int requestsPerMinute = 30, int requestsPerHour = 600,
    int minDelayMilliseconds = 1000, TimeProvider? timeProvider = null,
    TimeSpan? minuteWindow = null, TimeSpan? hourWindow = null)
    : ProviderRequestGovernor(requestsPerMinute, requestsPerHour, minDelayMilliseconds, timeProvider, minuteWindow, hourWindow);

public sealed class MicrosoftRequestGovernor(int requestsPerMinute = 30, int requestsPerHour = 600,
    int minDelayMilliseconds = 1000, TimeProvider? timeProvider = null,
    TimeSpan? minuteWindow = null, TimeSpan? hourWindow = null)
    : ProviderRequestGovernor(requestsPerMinute, requestsPerHour, minDelayMilliseconds, timeProvider, minuteWindow, hourWindow);
