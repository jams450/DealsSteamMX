#nullable enable
using System.Text.Json;
using Deals.API.Services.Jobs;

namespace JobsChecks;

/// <summary>
/// Queue seam with the production <see cref="JobRunQueue"/> behind it, so the checks exercise the real
/// reservation/dedup semantics and only the failure switches are scripted.
/// </summary>
internal sealed class FakeJobRunQueue : IJobRunQueue
{
    private readonly JobRunQueue _inner = new();

    public int ReserveCalls { get; private set; }
    public bool FailPublish { get; set; }
    public List<JobQueueItem> Published { get; } = [];

    public bool TryReserve(string job)
    {
        ReserveCalls++;
        return _inner.TryReserve(job);
    }

    public bool TryPublish(JobQueueItem item)
    {
        if (FailPublish)
        {
            return false;
        }

        Published.Add(item);
        return _inner.TryPublish(item);
    }

    public void Release(string job) => _inner.Release(job);

    public IReadOnlyList<JobQueueItem> Drain() => _inner.Drain();

    public IAsyncEnumerable<JobQueueItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _inner.ReadAllAsync(cancellationToken);
}

/// <summary>Run-log seam: records the start/finish pair instead of writing <c>job_runs</c>.</summary>
internal sealed class RecordingTracker : IJobRunTracker
{
    public long NextRunId { get; set; } = 100;
    public bool Busy { get; set; }
    public bool Throwing { get; set; }
    public List<string> Started { get; } = [];
    public List<(long JobRunId, string Status, string? Details)> Finished { get; } = [];

    public Task<long> StartAsync(string job, CancellationToken cancellationToken)
    {
        if (Throwing)
        {
            throw new InvalidOperationException("synthetic start failure");
        }

        Started.Add(job);
        return Task.FromResult(NextRunId++);
    }

    public Task FinishAsync(long jobRunId, string status, object? details, CancellationToken cancellationToken)
    {
        Finished.Add((jobRunId, status, details is null ? null : JsonSerializer.Serialize(details)));
        return Task.CompletedTask;
    }

    public Task<bool> HasRunningAsync(string job, CancellationToken cancellationToken) => Task.FromResult(Busy);
}

/// <summary>Executor seam: answers from a script instead of calling a provider.</summary>
internal sealed class FakeExecutor(string job, JobExecutionResult? result = null, Exception? failure = null) : IJobExecutor
{
    public string Job { get; } = job;
    public int Calls { get; private set; }
    public JobRunOptions? LastOptions { get; private set; }

    public Task<JobExecutionResult> ExecuteAsync(JobRunOptions options, CancellationToken cancellationToken)
    {
        Calls++;
        LastOptions = options;

        if (failure is not null)
        {
            return Task.FromException<JobExecutionResult>(failure);
        }

        return Task.FromResult(result ?? new JobExecutionResult(true, new { note = "ok" }));
    }
}
