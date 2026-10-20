using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;

namespace Deals.API.Services.Jobs;

/// <summary>
/// The write side of <c>job_runs</c> for manual runs, narrowed to what a submission needs. The scheduled
/// hosted services already take <see cref="JobRunLog"/> directly; this seam exists so the dispatcher can be
/// checked without a database and so the manual trigger has exactly one spelling.
/// </summary>
public interface IJobRunTracker
{
    /// <summary>Opens the run in <c>running</c> with the manual trigger and returns its id.</summary>
    Task<long> StartAsync(string job, CancellationToken cancellationToken);

    Task FinishAsync(long jobRunId, string status, object? details, CancellationToken cancellationToken);

    /// <summary>
    /// True when that job has a live <c>running</c> row. Rows older than
    /// <see cref="JobRunTracker.AbandonedAfter"/> are the record of a process that died mid-cycle and no
    /// longer block a new request — otherwise one killed process would disable the button forever.
    /// </summary>
    Task<bool> HasRunningAsync(string job, CancellationToken cancellationToken);
}

public sealed class JobRunTracker(JobRunLog log, IJobRunLogQueryService query) : IJobRunTracker
{
    /// <summary>
    /// A manual run of the longest job here (wishlist with its price pass) is expected in the tens of
    /// minutes. Six hours is far beyond that, so a row still <c>running</c> after it is a dead process, not
    /// a slow job. The plan calls that state <c>abandoned</c>; until it is persisted separately, this window
    /// is where the distinction lives.
    /// </summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(6);

    public Task<long> StartAsync(string job, CancellationToken cancellationToken) =>
        log.StartAsync(job, JobRunLog.ManualTrigger, cancellationToken);

    public Task FinishAsync(long jobRunId, string status, object? details, CancellationToken cancellationToken) =>
        log.FinishAsync(jobRunId, status, details, cancellationToken);

    public Task<bool> HasRunningAsync(string job, CancellationToken cancellationToken) =>
        query.HasRunningAsync(job, AbandonedAfter, cancellationToken);
}
