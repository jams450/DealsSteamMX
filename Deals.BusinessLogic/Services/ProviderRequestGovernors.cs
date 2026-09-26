namespace Deals.BusinessLogic.Services;

/// <summary>Independent local budget for ITAD. One unit equals one HTTP request.</summary>
public sealed class ItadRequestGovernor : IDisposable
{
    private readonly WindowGovernor governor;

    public ItadRequestGovernor(int requestsPerFiveMinutes, int maxBurst, int minDelayMilliseconds) =>
        governor = new WindowGovernor(
            [(TimeSpan.FromMinutes(5), requestsPerFiveMinutes)],
            maxBurst,
            minDelayMilliseconds);

    public ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken) => governor.AcquireAsync(1, cancellationToken);
    public void Dispose() => governor.Dispose();
}

/// <summary>Independent local budget for GG.deals. Units equal requested Steam app IDs/records.</summary>
public sealed class GgDealsRequestGovernor : IDisposable
{
    private readonly WindowGovernor governor;

    public GgDealsRequestGovernor(int recordsPerMinute, int recordsPerHour, int maxBurstRecords, int minDelayMilliseconds) =>
        governor = new WindowGovernor(
            [(TimeSpan.FromMinutes(1), recordsPerMinute), (TimeSpan.FromHours(1), recordsPerHour)],
            maxBurstRecords,
            minDelayMilliseconds);

    public ValueTask<IDisposable> AcquireAsync(int units, CancellationToken cancellationToken) => governor.AcquireAsync(units, cancellationToken);
    public void Dispose() => governor.Dispose();
}

internal sealed class WindowGovernor : IDisposable
{
    private readonly (TimeSpan Window, int Limit)[] windows;
    private readonly int maxBurst;
    private readonly TimeSpan minDelay;
    private readonly Queue<(DateTime At, int Units)> history = new();
    private readonly object sync = new();
    private DateTime lastRequestAt = DateTime.MinValue;

    public WindowGovernor((TimeSpan Window, int Limit)[] windows, int maxBurst, int minDelayMilliseconds)
    {
        this.windows = windows;
        this.maxBurst = maxBurst;
        minDelay = TimeSpan.FromMilliseconds(minDelayMilliseconds);
    }

    public async ValueTask<IDisposable> AcquireAsync(int units, CancellationToken cancellationToken)
    {
        if (units <= 0 || units > maxBurst)
            throw new ArgumentOutOfRangeException(nameof(units));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan wait;
            lock (sync)
            {
                var now = DateTime.UtcNow;
                Expire(now);
                var next = lastRequestAt == DateTime.MinValue
                    ? TimeSpan.Zero
                    : now + minDelay - lastRequestAt;
                var available = windows.Select(window =>
                    (window.Limit - history.Where(item => now - item.At < window.Window).Sum(item => item.Units)) >= units);
                if (next <= TimeSpan.Zero && available.All(value => value))
                {
                    history.Enqueue((now, units));
                    lastRequestAt = now;
                    return NoopLease.Instance;
                }

                wait = next > TimeSpan.Zero ? next : TimeSpan.FromMilliseconds(100);
                foreach (var window in windows)
                {
                    var used = history.Where(item => now - item.At < window.Window).Sum(item => item.Units);
                    if (used + units > window.Limit)
                    {
                        var oldest = history.First(item => now - item.At < window.Window);
                        wait = wait > window.Window - (now - oldest.At)
                            ? window.Window - (now - oldest.At)
                            : wait;
                    }
                }
            }

            await Task.Delay(wait, cancellationToken);
        }
    }

    private void Expire(DateTime now)
    {
        var longest = windows.Max(window => window.Window);
        while (history.Count > 0 && now - history.Peek().At >= longest)
            history.Dequeue();
    }

    public void Dispose() { }
    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();
        public void Dispose() { }
    }
}
