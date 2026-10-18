using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Background sweep of the gg.deals active bundle catalog. Disabled by default
/// (<see cref="GgDealsBundleOptions.Enabled"/>): the provider needs an API key and the job must not start
/// spending the shared GG.deals budget until an operator turns it on.
///
/// The whole cycle is serialized by a PostgreSQL session advisory lock, one cycle at a time across every
/// instance, under a key distinct from the wishlist, ntfy and global ITAD jobs. Provider calls happen
/// before any DB transaction (the writer opens its own short transaction only after the sweep completed
/// in memory). Every run is isolated in its own scope and swallows its own failures so the host stays
/// up; when the provider fails, the cursor expires or the enumeration truncates, the service writes
/// nothing and the previous snapshot and its observed timestamp stand. Provider exceptions are logged by
/// type only: a raw message must never carry the API key or a request URL.
/// </summary>
public sealed class GgDealsBundleSyncJob(
    IServiceScopeFactory scopeFactory,
    IOptions<GgDealsBundleOptions> options,
    ILogger<GgDealsBundleSyncJob> logger) : BackgroundService
{
    /// <summary>
    /// The sweep's PostgreSQL session advisory lock key, owned by <see cref="GgDealsBundleSyncService"/>
    /// so the checks can assert it never collides with the wishlist (<c>7_831_442_091</c>), ntfy
    /// (<c>7_831_442_092</c>) or global ITAD bundle (<c>7_831_442_093</c>) locks.
    /// </summary>
    private const long AdvisoryLockKey = GgDealsBundleSyncService.AdvisoryLockKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var job = options.Value;
        if (!job.Enabled)
        {
            logger.LogInformation("[bundles.ggdeals] disabled by configuration; job will not run");
            return;
        }

        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, job.StartupDelaySeconds));
        try
        {
            await Task.Delay(startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var trigger = JobRunLog.StartupTrigger;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (trigger == JobRunLog.StartupTrigger &&
                    await RanRecentlyAsync(TimeSpan.FromHours(Math.Max(0, job.MinHoursBetweenRuns)), stoppingToken))
                {
                    logger.LogInformation(
                        "[bundles.ggdeals] a run newer than {Hours}h exists; recovery pass skipped",
                        job.MinHoursBetweenRuns);
                }
                else
                {
                    await RunOnceAsync(trigger, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            trigger = JobRunLog.ScheduledTrigger;

            var interval = TimeSpan.FromHours(Math.Max(1, job.RefreshHours));
            var jitterMinutes = job.JitterMinutes <= 0 ? 0 : Random.Shared.Next(job.JitterMinutes + 1);
            var jitter = TimeSpan.FromMinutes(jitterMinutes);
            try
            {
                await Task.Delay(interval + jitter, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> RanRecentlyAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<JobRunLog>()
                .RanWithinAsync(GgDealsBundleSyncService.JobName, window, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail open: an unreadable log means "cannot tell", and running the sweep is the pre-gate
            // behaviour. Throwing here would fault ExecuteAsync and take the API down instead.
            logger.LogError(
                "[bundles.ggdeals] could not read job_runs (errorType={ErrorType}); running the recovery pass",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task RunOnceAsync(string trigger, CancellationToken cancellationToken)
    {
        var jobRunId = 0L;
        await using var lockScope = scopeFactory.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var syncLock = new PostgresAdvisoryLock(lockDb, AdvisoryLockKey);
        if (!await syncLock.TryAcquireAsync(cancellationToken))
        {
            logger.LogInformation("[bundles.ggdeals] another process owns the sweep lock; cycle skipped");
            return;
        }

        try
        {
            jobRunId = await lockScope.ServiceProvider.GetRequiredService<JobRunLog>()
                .StartAsync(GgDealsBundleSyncService.JobName, trigger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail open. An unwritable log must never stop the sweep; the gate fails open too, so the pass
            // is then replayed on every start - noisy but better than a job that dies because the
            // job_runs migration has not been applied yet.
            logger.LogError(
                "[bundles.ggdeals] could not record the run (errorType={ErrorType}); continuing without a log entry",
                exception.GetType().Name);
        }

        var status = JobRunStatuses.Ok;
        object? details = null;
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<GgDealsBundleSyncService>()
                .SyncAsync(cancellationToken);
            details = new
            {
                result.Completed,
                Reason = result.IncompleteReason.ToString(),
                result.Pages,
                result.Bundles,
                result.Written,
                result.Removed
            };

            if (!result.Completed)
            {
                // The sweep deliberately preserved the previous snapshot; record it as failed so the
                // degradation is visible in job_runs without pretending the job crashed.
                status = JobRunStatuses.Failed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the run row stays 'running', the honest record of an interrupted pass.
            return;
        }
        catch (Exception exception)
        {
            status = JobRunStatuses.Failed;
            // Only the exception type is recorded/logged: a provider exception message could otherwise
            // carry a request URL and therefore the API key into job_runs, logs or the admin UI.
            details = new { errorType = exception.GetType().Name };
            logger.LogError(
                "[bundles.ggdeals] sweep failed (errorType={ErrorType})",
                exception.GetType().Name);
        }

        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JobRunLog>()
                .FinishAsync(jobRunId, status, details, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "[bundles.ggdeals] could not record the run outcome (errorType={ErrorType})",
                exception.GetType().Name);
        }
    }
}
