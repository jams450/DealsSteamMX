using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Background sweep of the global ITAD deals catalog. Disabled by default
/// (<see cref="GlobalDealOptions.Enabled"/>): the feed turns on only after the migration is applied and
/// the deployment explicitly opts in. It is the only caller of <see cref="GlobalDealSyncService"/>:
/// the paginated <c>deals/v2</c> catalog is far too large to be driven by a user request, so it runs on
/// its own conservative schedule and spends the shared ITAD budget from the background priority.
///
/// The whole cycle is serialized by a PostgreSQL session advisory lock
/// (<see cref="GlobalDealSyncService.AdvisoryLockKey"/>), one cycle at a time across every instance.
/// Provider calls happen before any DB transaction. Every run is isolated in its own scope and swallows
/// its own failures so the host stays up; when the provider fails or the sweep truncates, the service
/// writes nothing and the previous snapshot and its observed timestamp stand.
/// </summary>
public sealed class GlobalDealSyncJob(
    IServiceScopeFactory scopeFactory,
    IOptions<GlobalDealOptions> options,
    ILogger<GlobalDealSyncJob> logger) : BackgroundService
{
    private const long AdvisoryLockKey = GlobalDealSyncService.AdvisoryLockKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var deals = options.Value;
        if (!deals.Enabled)
        {
            logger.LogInformation("[deals.global] disabled by configuration; job will not run");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, deals.StartupDelaySeconds)), stoppingToken);
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
                    await RanRecentlyAsync(TimeSpan.FromHours(Math.Max(0, deals.MinHoursBetweenRuns)), stoppingToken))
                {
                    logger.LogInformation(
                        "[deals.global] a run newer than {Hours}h exists; recovery pass skipped",
                        deals.MinHoursBetweenRuns);
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

            var interval = TimeSpan.FromHours(Math.Max(1, deals.RefreshHours));
            var jitterMinutes = deals.JitterMinutes <= 0 ? 0 : Random.Shared.Next(deals.JitterMinutes + 1);
            try
            {
                await Task.Delay(interval + TimeSpan.FromMinutes(jitterMinutes), stoppingToken);
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
                .RanWithinAsync(GlobalDealSyncService.JobName, window, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[deals.global] could not read job_runs; running the recovery pass");
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
            logger.LogInformation("[deals.global] another process owns the sweep lock; cycle skipped");
            return;
        }

        try
        {
            jobRunId = await lockScope.ServiceProvider.GetRequiredService<JobRunLog>()
                .StartAsync(GlobalDealSyncService.JobName, trigger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[deals.global] could not record the run; continuing without a log entry");
        }

        var status = JobRunStatuses.Ok;
        object? details = null;
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<GlobalDealSyncService>()
                .SyncAsync(cancellationToken);
            details = new
            {
                result.Completed,
                Reason = result.IncompleteReason.ToString(),
                result.Pages,
                result.Deals,
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
            logger.LogError(exception, "[deals.global] sweep failed");
        }

        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JobRunLog>()
                .FinishAsync(jobRunId, status, details, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[deals.global] could not record the run outcome");
        }
    }
}
