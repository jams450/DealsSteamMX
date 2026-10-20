using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Deals.API.Services.Jobs;

/// <summary>
/// Hosted consumer of <see cref="IJobRunQueue"/>. This is the piece that keeps a manual run off the HTTP
/// request: the controller only records and queues, and this service performs the work with the same
/// advisory lock and the same <c>job_runs</c> row the scheduled cycle uses.
///
/// One item at a time per process on purpose. The operator asked for a run, not for a burst: serialising
/// here keeps two manual jobs from spending the same provider budget simultaneously, and the advisory lock
/// already serialises against the scheduled cycle and against other instances.
/// </summary>
public sealed class JobRunDispatchService(
    IJobRunQueue queue,
    IServiceScopeFactory scopeFactory,
    JobCatalog catalog,
    ILogger<JobRunDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.ReadAllAsync(stoppingToken))
            {
                await DispatchAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }

        await FailPendingAsync();
    }

    private async Task DispatchAsync(JobQueueItem item, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var definition = catalog.Find(item.Job);
            if (definition?.AdvisoryLockKey is { } lockKey)
            {
                var lockDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await using var jobLock = new PostgresAdvisoryLock(lockDb, lockKey);
                if (!await jobLock.TryAcquireAsync(cancellationToken))
                {
                    // A scheduled cycle (or another instance) owns the job right now. Report it instead of
                    // running concurrently: the whole point of the shared key is that only one runs.
                    logger.LogInformation(
                        "[jobs.manual] job={Job} is locked by another worker; run {JobRunId} skipped",
                        item.Job,
                        item.JobRunId);
                    await FinishAsync(
                        scope.ServiceProvider,
                        item.JobRunId,
                        JobRunStatuses.Failed,
                        new { reason = "job_locked", job = item.Job },
                        cancellationToken);
                    return;
                }

                await scope.ServiceProvider.GetRequiredService<JobExecutionRunner>()
                    .RunAsync(item, cancellationToken);
                return;
            }

            await scope.ServiceProvider.GetRequiredService<JobExecutionRunner>()
                .RunAsync(item, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-run: the row stays 'running', the honest record of an interrupted pass.
            throw;
        }
        catch (Exception exception)
        {
            // The host must survive a manual run: the same policy as every scheduled job.
            logger.LogError(
                exception,
                "[jobs.manual] dispatch failed for job={Job} jobRunId={JobRunId}",
                item.Job,
                item.JobRunId);

            // The executor already closes its own row on failure, so reaching here means the scope or the
            // lock could not be built. Close the row too, or it would stay 'running' with no explanation
            // until the abandoned window expires.
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await FinishAsync(
                    scope.ServiceProvider,
                    item.JobRunId,
                    JobRunStatuses.Failed,
                    new { reason = "dispatch_failed", job = item.Job, errorType = exception.GetType().Name },
                    CancellationToken.None);
            }
            catch (Exception closeException)
            {
                logger.LogWarning(
                    closeException,
                    "[jobs.manual] could not close run {JobRunId} after a dispatch failure",
                    item.JobRunId);
            }
        }
        finally
        {
            queue.Release(item.Job);
        }
    }

    /// <summary>
    /// On shutdown, closes the runs that were queued but never started. Leaving them <c>running</c> would be
    /// a lie about work that never happened, and the stale-row window would then block the job for hours.
    /// Bounded: only what is still in the channel (at most <see cref="JobRunQueue.Capacity"/>) and a short
    /// timeout, because this runs while the host is stopping.
    /// </summary>
    private async Task FailPendingAsync()
    {
        var pending = queue.Drain();
        if (pending.Count == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var item in pending)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await FinishAsync(
                    scope.ServiceProvider,
                    item.JobRunId,
                    JobRunStatuses.Failed,
                    new { reason = "host_shutdown", job = item.Job },
                    timeout.Token);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "[jobs.manual] could not close the unstarted run {JobRunId} during shutdown",
                    item.JobRunId);
            }
        }
    }

    private static async Task FinishAsync(
        IServiceProvider services,
        long jobRunId,
        string status,
        object details,
        CancellationToken cancellationToken)
    {
        var tracker = services.GetRequiredService<IJobRunTracker>();
        await tracker.FinishAsync(jobRunId, status, details, cancellationToken);
    }
}
