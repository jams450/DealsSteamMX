using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Diagnostics;
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
/// An executor whose work is scoped to one person (spec S5). The requester arrives from the queue item — it was
/// captured from the access token when the run was submitted — so the batch can never be widened by the payload
/// nor read from an ambient request context in the background.
///
/// The plain <see cref="IJobExecutor"/> entry point stays available because the registration contract is one
/// executor per job; calling it without a requester must fail closed instead of running a wider pass.
/// </summary>
public interface IUserScopedJobExecutor : IJobExecutor
{
    /// <param name="requestedByUserId">
    /// The authenticated requester, always &gt; 0: <see cref="JobExecutionRunner"/> refuses to call this member
    /// without it.
    /// </param>
    Task<JobExecutionResult> ExecuteAsync(
        JobRunOptions options,
        int requestedByUserId,
        CancellationToken cancellationToken);
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
    ILogger<JobExecutionRunner> logger,
    // Scoped observer of the same run (spec S2/S3). Optional so a hand-built runner still works; the run it
    // observes is the one in this scope, so its report can never contain another run's events.
    ISyncDiagnosticsCollector? diagnostics = null)
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

        // The requester was captured from the access token when the run was queued. For a user-scoped job it is
        // mandatory: without it the run is refused here, before a provider call, instead of silently widening
        // the batch.
        var requestedBy = item.RequestedByUserId is > 0 ? item.RequestedByUserId : null;

        string status;
        object details;
        try
        {
            JobExecutionResult result;
            if (executor is IUserScopedJobExecutor scoped)
            {
                if (requestedBy is null)
                {
                    logger.LogWarning(
                        "[jobs.manual] job={Job} jobRunId={JobRunId} has no authenticated requester; refused",
                        item.Job,
                        item.JobRunId);
                    await FinishAsync(
                        item.JobRunId,
                        JobRunStatuses.Failed,
                        new
                        {
                            reason = "requester_required",
                            job = item.Job,
                            requested = item.Options.ToDetails()
                        },
                        cancellationToken);
                    return;
                }

                result = await scoped.ExecuteAsync(item.Options, requestedBy.Value, cancellationToken);
                status = result.Succeeded ? JobRunStatuses.Ok : JobRunStatuses.Failed;
                details = new
                {
                    // Kept in the closing record too: FinishAsync overwrites the row's details, and who asked
                    // for a personal batch is part of what happened.
                    requestedBy = requestedBy.Value,
                    requested = item.Options.ToDetails(),
                    result = result.Details
                };
            }
            else
            {
                result = await executor.ExecuteAsync(item.Options, cancellationToken);
                status = result.Succeeded ? JobRunStatuses.Ok : JobRunStatuses.Failed;
                details = new
                {
                    requested = item.Options.ToDetails(),
                    result = result.Details
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the row stays 'running', the honest record of an interrupted pass.
            throw;
        }
        catch (Exception exception)
        {
            status = JobRunStatuses.Failed;
            // A personal run keeps its owner in the closing record too; every other job's failure shape is
            // left exactly as it was.
            var report = Report();
            details = requestedBy is { } requester
                ? new
                {
                    requestedBy = requester,
                    requested = item.Options.ToDetails(),
                    errorType = exception.GetType().Name,
                    error = exception.Message,
                    // What the run observed before it failed: without it the failure record says only that the
                    // executor threw, which is exactly the half-information this unit exists to remove.
                    diagnostics = report
                }
                : new
                {
                    requested = item.Options.ToDetails(),
                    errorType = exception.GetType().Name,
                    error = exception.Message,
                    diagnostics = report
                };
            logger.LogError(
                exception,
                "[jobs.manual] job={Job} jobRunId={JobRunId} failed",
                item.Job,
                item.JobRunId);
        }

        await FinishAsync(item.JobRunId, status, details, cancellationToken);
    }

    /// <summary>
    /// The report of the run this runner is executing, or null when it observed no provider phase: a run that never
    /// reached a provider must not carry an empty document that could be read as "nothing failed".
    /// </summary>
    private SyncDiagnosticsSnapshot? Report() =>
        diagnostics is { HasEvents: true } collector ? collector.BuildSnapshot() : null;

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
