using Deals.BusinessLogic.Interfaces;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>Independent local budget for ITAD. One unit equals one HTTP request.</summary>
public sealed class ItadRequestGovernor : IDisposable
{
    private readonly WindowGovernor governor;
    private readonly ILogger<ItadRequestGovernor> logger;

    public ItadRequestGovernor(
        int requestsPerFiveMinutes,
        int maxBurst,
        int minDelayMilliseconds,
        ILogger<ItadRequestGovernor> logger)
    {
        governor = new WindowGovernor(
            [(TimeSpan.FromMinutes(5), requestsPerFiveMinutes)],
            maxBurst,
            minDelayMilliseconds);
        this.logger = logger;
    }

    public async ValueTask<IDisposable> AcquireAsync(ItadRequestPriority priority, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var lease = await governor.AcquireAsync(1, priority == ItadRequestPriority.Interactive, cancellationToken);
            logger.LogInformation(
                "[itad.queue] priority={Priority} outcome=admitted elapsedMs={ElapsedMs}",
                priority,
                stopwatch.ElapsedMilliseconds);
            return lease;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "[itad.queue] priority={Priority} outcome=cancelled elapsedMs={ElapsedMs}",
                priority,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

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

    public ValueTask<IDisposable> AcquireAsync(int units, CancellationToken cancellationToken) => governor.AcquireAsync(units, interactive: false, cancellationToken);
    public void Dispose() => governor.Dispose();
}

internal sealed class WindowGovernor : IDisposable
{
    private readonly (TimeSpan Window, int Limit)[] windows;
    private readonly int maxBurst;
    private readonly TimeSpan minDelay;
    private readonly Queue<(DateTime At, int Units)> history = new();
    private readonly LinkedList<Waiter> interactiveWaiters = new();
    private readonly LinkedList<Waiter> backgroundWaiters = new();
    private readonly object sync = new();
    private DateTime lastRequestAt = DateTime.MinValue;

    public WindowGovernor((TimeSpan Window, int Limit)[] windows, int maxBurst, int minDelayMilliseconds)
    {
        this.windows = windows;
        this.maxBurst = maxBurst;
        minDelay = TimeSpan.FromMilliseconds(minDelayMilliseconds);
    }

    public async ValueTask<IDisposable> AcquireAsync(int units, bool interactive, CancellationToken cancellationToken)
    {
        if (units <= 0 || units > maxBurst)
            throw new ArgumentOutOfRangeException(nameof(units));

        var waiter = new Waiter(units);
        lock (sync)
        {
            waiter.Node = (interactive ? interactiveWaiters : backgroundWaiters).AddLast(waiter);
        }

        try
        {
            while (!waiter.Admitted.Task.IsCompleted)
            {
                TimeSpan wait;
                lock (sync)
                {
                    var now = DateTime.UtcNow;
                    Expire(now);
                    AdmitNext(now);
                    if (waiter.Admitted.Task.IsCompleted)
                    {
                        break;
                    }

                    wait = GetWait(now, waiter.Units);
                }

                await Task.Delay(wait, cancellationToken);
            }

            return await waiter.Admitted.Task;
        }
        finally
        {
            lock (sync)
            {
                if (waiter.Node?.List is not null)
                {
                    waiter.Node.List.Remove(waiter.Node);
                    waiter.Node = null;
                }
            }
        }
    }

    private void AdmitNext(DateTime now)
    {
        var waiter = interactiveWaiters.First?.Value ?? backgroundWaiters.First?.Value;
        if (waiter is null || !CanAdmit(now, waiter.Units))
        {
            return;
        }

        waiter.Node!.List!.Remove(waiter.Node);
        waiter.Node = null;
        history.Enqueue((now, waiter.Units));
        lastRequestAt = now;
        waiter.Admitted.TrySetResult(NoopLease.Instance);
    }

    private bool CanAdmit(DateTime now, int units) =>
        (lastRequestAt == DateTime.MinValue || now - lastRequestAt >= minDelay) &&
        windows.All(window =>
            window.Limit - history.Where(item => now - item.At < window.Window).Sum(item => item.Units) >= units);

    private TimeSpan GetWait(DateTime now, int units)
    {
        var wait = lastRequestAt == DateTime.MinValue
            ? TimeSpan.FromMilliseconds(100)
            : lastRequestAt + minDelay - now;
        if (wait <= TimeSpan.Zero)
        {
            wait = TimeSpan.FromMilliseconds(100);
        }

        foreach (var window in windows)
        {
            var used = history.Where(item => now - item.At < window.Window).Sum(item => item.Units);
            if (used + units > window.Limit)
            {
                var oldest = history.First(item => now - item.At < window.Window);
                var untilAvailable = window.Window - (now - oldest.At);
                if (untilAvailable > wait)
                {
                    wait = untilAvailable;
                }
            }
        }

        return wait;
    }

    private void Expire(DateTime now)
    {
        var longest = windows.Max(window => window.Window);
        while (history.Count > 0 && now - history.Peek().At >= longest)
            history.Dequeue();
    }

    public void Dispose() { }

    private sealed class Waiter(int units)
    {
        public int Units { get; } = units;
        public LinkedListNode<Waiter>? Node { get; set; }
        public TaskCompletionSource<IDisposable> Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();
        public void Dispose() { }
    }
}
