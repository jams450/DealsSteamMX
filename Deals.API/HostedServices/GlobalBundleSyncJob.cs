using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Background sweep of the global ITAD bundle catalog. It is the only caller of
/// <see cref="GlobalBundleSyncService"/>: the paginated <c>bundles/v1</c> catalog is far too large to be
/// driven by a user request, so it runs on its own conservative schedule and spends the shared ITAD
/// budget from the background priority.
///
/// The whole cycle is serialized by a PostgreSQL session advisory lock, one cycle at a time across every
/// instance. The provider calls happen before any DB transaction (the writer opens its own short
/// transaction only after the sweep completed in memory). Every run is isolated in its own scope and
/// swallows its own failures so the host stays up; when the provider fails or the sweep truncates, the
/// service writes nothing and the previous snapshot and its observed timestamp stand.
/// </summary>
public sealed class GlobalBundleSyncJob(
    IServiceScopeFactory scopeFactory,
    IOptions<GlobalBundleOptions> options,
    ILogger<GlobalBundleSyncJob> logger) : BackgroundService
{
    /// <summary>
    /// The sweep's PostgreSQL session advisory lock key, owned by <see cref="GlobalBundleSyncService"/> so
    /// the checks can assert it never collides with the wishlist (<c>7_831_442_091</c>) or ntfy
    /// (<c>7_831_442_092</c>) locks. Sharing a key would make two independent jobs skip each other.
    /// </summary>
    private const long AdvisoryLockKey = GlobalBundleSyncService.AdvisoryLockKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var global = options.Value;
        if (!global.Enabled)
        {
            logger.LogInformation("[bundles.global] disabled by configuration; job will not run");
            return;
        }

        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, global.StartupDelaySeconds));
        try
        {
            await Task.Delay(startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Recovery pass: a restart shortly after a scheduled slot must not lose that slot, but the gate
        // stops a deploy or crash loop from replaying a sweep that already ran.
        var trigger = JobRunLog.StartupTrigger;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (trigger == JobRunLog.StartupTrigger &&
                    await RanRecentlyAsync(TimeSpan.FromHours(Math.Max(0, global.MinHoursBetweenRuns)), stoppingToken))
                {
                    logger.LogInformation(
                        "[bundles.global] a run newer than {Hours}h exists; recovery pass skipped",
                        global.MinHoursBetweenRuns);
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

            var interval = TimeSpan.FromHours(Math.Max(1, global.RefreshHours));
            var jitterMinutes = global.JitterMinutes <= 0 ? 0 : Random.Shared.Next(global.JitterMinutes + 1);
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
                .RanWithinAsync(GlobalBundleSyncService.JobName, window, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail open: an unreadable log means "cannot tell", and running the sweep is the pre-gate
            // behaviour. Throwing here would fault ExecuteAsync and take the API down instead.
            logger.LogError(exception, "[bundles.global] could not read job_runs; running the recovery pass");
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
            logger.LogInformation("[bundles.global] another process owns the sweep lock; cycle skipped");
            return;
        }

        try
        {
            jobRunId = await lockScope.ServiceProvider.GetRequiredService<JobRunLog>()
                .StartAsync(GlobalBundleSyncService.JobName, trigger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail open. An unwritable log must never stop the sweep; the gate fails open too, so the pass
            // is then replayed on every start — noisy but better than a job that dies because the job_runs
            // migration has not been applied yet.
            logger.LogError(exception, "[bundles.global] could not record the run; continuing without a log entry");
        }

        var status = JobRunStatuses.Ok;
        object? details = null;
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<GlobalBundleSyncService>()
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
            details = new { error = exception.Message };
            logger.LogError(exception, "[bundles.global] sweep failed");
        }

        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JobRunLog>()
                .FinishAsync(jobRunId, status, details, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[bundles.global] could not record the run outcome");
        }
    }
}
