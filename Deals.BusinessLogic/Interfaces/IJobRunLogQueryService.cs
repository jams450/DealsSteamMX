using Deals.BusinessLogic.Models.Jobs;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Read side of <c>job_runs</c>. It never writes: the jobs and the manual dispatcher own the start/finish
/// pair, and this interface only answers "what ran", "what is running" and "what is the newest run of this
/// job". Split from the writer so a query can be served by a read-only dependency and checked without a
/// database.
/// </summary>
public interface IJobRunLogQueryService
{
    Task<JobRunQueryPage> QueryAsync(JobRunQueryFilter filter, CancellationToken cancellationToken);

    Task<JobRunQueryItem?> GetAsync(long jobRunId, CancellationToken cancellationToken);

    /// <summary>Newest run of every requested job. Jobs without any run are absent from the result.</summary>
    Task<IReadOnlyList<JobRunLatest>> GetLatestByJobAsync(
        IReadOnlyCollection<string> jobs,
        CancellationToken cancellationToken);

    /// <summary>
    /// True when <paramref name="job"/> has a row in <c>running</c> that is not older than
    /// <paramref name="abandonedAfter"/>. A row older than that is the record of a process that died
    /// mid-cycle: it still marks a run that never finished, but it must not block the operator forever.
    /// </summary>
    Task<bool> HasRunningAsync(string job, TimeSpan abandonedAfter, CancellationToken cancellationToken);
}
