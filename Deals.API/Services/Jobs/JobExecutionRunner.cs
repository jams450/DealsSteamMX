using Deals.Models.Entities;

namespace Deals.API.Services.Jobs;

/// <summary>
/// Outcome of one job execution. <see cref="Succeeded"/> is false when the job ran but left the work
/// degraded (the bundle sweeps preserve the previous snapshot instead of writing a partial catalog), so a
/// degraded cycle is never recorded as <c>ok</c>.
/// </summary>
public sealed record JobExecutionResult(bool Succeeded, object? Details);

/// <summary>
/// The work of one registered job, with no scheduling, no locking and no logging of its own: the scheduled
/// hosted service and the manual dispatcher both call this, which is what makes a manual run the same run.
/// </summary>
public interface IJobExecutor
{
    /// <summary>Stable job key, matching <see cref="JobCatalog"/> and <c>job_runs.job</c>.</summary>
    string Job { get; }

    Task<JobExecutionResult> ExecuteAsync(JobRunOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Executes one queued manual run and records its outcome. The advisory lock is taken by the dispatcher
/// before this runs, so the only responsibilities here are "find the executor", "run it", and "close the
/// row exactly once".
///
/// Failure policy: a job must never take the host down, and a provider exception message must never travel
/// unsanitised into the admin UI. The row records the exception type and the message; the read path
/// (<see cref="JobDetailsSanitizer"/>) redacts anything that looks like a credential.
/// </summary>
public sealed class JobExecutionRunner(
    JobCatalog catalog,
    IEnumerable<IJobExecutor> executors,
    IJobRunTracker tracker,
    ILogger<JobExecutionRunner> logger)
{
    public async Task RunAsync(JobQueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var executor = executors.FirstOrDefault(candidate =>
            string.Equals(candidate.Job, item.Job, StringComparison.Ordinal));

        if (catalog.Find(item.Job) is null || executor is null)
        {
            // The queue item was built from the catalog, so this only happens across a deployment that
            // removed a job while it was pending. The row is closed with the name, not left running.
            logger.LogWarning("[jobs.manual] no executor for job={Job}", item.Job);
            await FinishAsync(
                item.JobRunId,
                JobRunStatuses.Failed,
                new { reason = "unknown_job", job = item.Job, requested = item.Options.ToDetails() },
                cancellationToken);
            return;
        }

        string status;
        object details;
        try
        {
            var result = await executor.ExecuteAsync(item.Options, cancellationToken);
            status = result.Succeeded ? JobRunStatuses.Ok : JobRunStatuses.Failed;
            details = new
            {
                requested = item.Options.ToDetails(),
                result = result.Details
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the row stays 'running', the honest record of an interrupted pass.
            throw;
        }
        catch (Exception exception)
        {
            status = JobRunStatuses.Failed;
            details = new
            {
                requested = item.Options.ToDetails(),
                errorType = exception.GetType().Name,
                error = exception.Message
            };
            logger.LogError(
                exception,
                "[jobs.manual] job={Job} jobRunId={JobRunId} failed",
                item.Job,
                item.JobRunId);
        }

        await FinishAsync(item.JobRunId, status, details, cancellationToken);
    }

    private async Task FinishAsync(long jobRunId, string status, object details, CancellationToken cancellationToken)
    {
        try
        {
            await tracker.FinishAsync(jobRunId, status, details, cancellationToken);
        }
        catch (Exception exception)
        {
            // A log write never fails the execution: the row keeps its started_at and the dispatcher frees
            // the job either way.
            logger.LogError(
                exception,
                "[jobs.manual] could not record the outcome of run {JobRunId}",
                jobRunId);
        }
    }
}
