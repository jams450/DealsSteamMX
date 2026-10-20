using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Deals.API.Services.Jobs;

/// <summary>One accepted manual request, waiting for the dispatcher.</summary>
public sealed record JobQueueItem(string Job, long JobRunId, JobRunOptions Options);

/// <summary>
/// In-process hand-off between the HTTP request that asks for a run and the hosted dispatcher that performs
/// it. The request reserves the job before any database row exists, publishes the run id, and returns; the
/// reservation is what makes two concurrent requests answer 409 instead of starting the same job twice.
///
/// It is per process, which is enough: the database check and the PostgreSQL advisory lock are the
/// cross-instance guards, and this queue only has to be exact inside one host.
/// </summary>
public interface IJobRunQueue
{
    /// <summary>
    /// Claims the job for one submission. False means a run of that job is already reserved, queued or
    /// executing in this process.
    /// </summary>
    bool TryReserve(string job);

    /// <summary>Hands a reserved job to the dispatcher. False also releases nothing: the caller releases.</summary>
    bool TryPublish(JobQueueItem item);

    /// <summary>Frees the job, whether the run finished, failed or never started.</summary>
    void Release(string job);

    /// <summary>Removes everything still waiting (host shutdown) and frees those jobs.</summary>
    IReadOnlyList<JobQueueItem> Drain();

    IAsyncEnumerable<JobQueueItem> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Bounded in-memory queue plus the set of jobs this process currently owns. The bound is small on purpose:
/// a manual run is an operator action, and a queue of 32 pending jobs would mean the bound is not doing its
/// job. A full queue is refused, not awaited — the HTTP request must answer, not hang.
/// </summary>
public sealed class JobRunQueue : IJobRunQueue
{
    public const int Capacity = 32;

    private readonly Channel<JobQueueItem> _channel = Channel.CreateBounded<JobQueueItem>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public bool TryReserve(string job) =>
        !string.IsNullOrWhiteSpace(job) && _active.TryAdd(job, 0);

    public bool TryPublish(JobQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Only a reserved job may be published: publishing without the reservation would bypass the
        // duplicate guard this queue exists to provide.
        if (!_active.ContainsKey(item.Job))
        {
            return false;
        }

        return _channel.Writer.TryWrite(item);
    }

    public void Release(string job)
    {
        if (!string.IsNullOrWhiteSpace(job))
        {
            _active.TryRemove(job, out _);
        }
    }

    public IReadOnlyList<JobQueueItem> Drain()
    {
        var pending = new List<JobQueueItem>();
        while (_channel.Reader.TryRead(out var item))
        {
            pending.Add(item);
            _active.TryRemove(item.Job, out _);
        }

        return pending;
    }

    public IAsyncEnumerable<JobQueueItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
