using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Periodic wishlist synchronization. Every cycle is isolated in its own scope, and each of the two
/// passes swallows its own failure so a list-sync error never cancels the price refresh (and vice versa),
/// and neither takes the host down.
///
/// Each cycle is recorded in <c>job_runs</c>. The recovery pass of a host start is the only gated one:
/// a deploy, a crash loop or a local debug session must not replay a pass that already ran inside
/// <see cref="WishlistOptions.MinHoursBetweenRuns"/>. The daily slot is never skipped, so the gate can
/// never cost the day's run.
/// </summary>
public sealed class WishlistSyncJob(
    IServiceScopeFactory scopeFactory,
    IOptions<WishlistOptions> options,
    ILogger<WishlistSyncJob> logger) : BackgroundService
{
    private const string Job = JobRunLog.WishlistSync;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wishlist = options.Value;
        if (!wishlist.Enabled)
        {
            logger.LogInformation("[wishlist.sync] disabled by configuration; job will not run");
            return;
        }

        // Resolved before the delay and outside the pass loop: a zone the host cannot resolve means there is
        // no schedule to keep. Throwing here would abort ExecuteAsync and, with the default
        // BackgroundServiceExceptionBehavior, take the whole API down instead of just this job.
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(wishlist.TimeZoneId);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[wishlist.sync] TimeZoneId '{TimeZoneId}' is not resolvable; job will not run",
                wishlist.TimeZoneId);
            return;
        }

        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, wishlist.StartupDelaySeconds));
        var jitter = TimeSpan.FromMinutes(wishlist.JitterMinutes);
        var recoveryWindow = TimeSpan.FromHours(Math.Max(0, wishlist.MinHoursBetweenRuns));

        try
        {
            await Task.Delay(startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Recovery pass: a restart shortly after the scheduled hour must not lose that day's run. That is the
        // whole point, and also why it is gated: a restart long after the hour has nothing to recover.
        var trigger = JobRunLog.StartupTrigger;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (trigger == JobRunLog.StartupTrigger && await RanRecentlyAsync(recoveryWindow, stoppingToken))
                {
                    logger.LogInformation(
                        "[wishlist.sync] a run newer than {Hours}h exists; recovery pass skipped",
                        recoveryWindow.TotalHours);
                }
                else
                {
                    await RunPassesAsync(trigger, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            trigger = JobRunLog.ScheduledTrigger;

            var nowUtc = DateTimeOffset.UtcNow;
            var nextRun = WishlistSchedule.NextRunAt(nowUtc, zone, wishlist.RunAtHour, jitter);
            logger.LogInformation(
                "[wishlist.sync] next run at {NextRun:o} ({TimeZoneId})",
                nextRun,
                wishlist.TimeZoneId);

            try
            {
                await Task.Delay(nextRun - nowUtc, stoppingToken);
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
            var jobRunLog = scope.ServiceProvider.GetRequiredService<JobRunLog>();

            return await jobRunLog.RanWithinAsync(Job, window, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail open. An unreadable log means "cannot tell", and running a pass is the behaviour the job
            // had before this table existed. Throwing would fault ExecuteAsync and, with the default
            // BackgroundServiceExceptionBehavior, take the whole API down instead of just skipping a cycle.
            logger.LogError(exception, "[wishlist.sync] could not read job_runs; running the recovery pass");
            return false;
        }
    }

    private async Task RunPassesAsync(string trigger, CancellationToken cancellationToken)
    {
        // 0 means "not recorded": FinishAsync then finds no row and returns, and the cycle still runs.
        var jobRunId = 0L;
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            jobRunId = await scope.ServiceProvider.GetRequiredService<JobRunLog>()
                .StartAsync(Job, trigger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail open. An unwritable log must never stop the sync: the gate fails open too, so the pass is
            // then replayed on every start — noisy and visible in the logs, but far better than a job that
            // dies silently because the job_runs migration has not been applied yet.
            logger.LogError(exception, "[wishlist.sync] could not record the run; continuing without a log entry");
        }

        var outcome = new Dictionary<string, object?>(StringComparer.Ordinal);
        var failed = false;

        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishlistSyncService>();

            try
            {
                var list = await service.SyncListAsync(cancellationToken);
                outcome["list"] = new
                {
                    list.State,
                    list.ItemCount,
                    list.Added,
                    list.Updated,
                    list.Removed,
                    list.FetchedFromSteam,
                    list.FetchFailed
                };
                logger.LogInformation(
                    "[wishlist.sync] list state={State} items={Items} added={Added} updated={Updated} removed={Removed}",
                    list.State,
                    list.ItemCount,
                    list.Added,
                    list.Updated,
                    list.Removed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down: the run row stays 'running', which is the honest record of an interrupted pass.
                return;
            }
            catch (Exception exception)
            {
                failed = true;
                outcome["listError"] = exception.Message;
                logger.LogError(exception, "[wishlist.sync] list sync failed");
            }

            try
            {
                var refresh = await service.RefreshWishedGamesAsync(cancellationToken);
                outcome["refresh"] = new { refresh.Refreshed, refresh.Failed };
                logger.LogInformation(
                    "[wishlist.sync] refresh refreshed={Refreshed} failed={Failed}",
                    refresh.Refreshed,
                    refresh.Failed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
                return;
            }
            catch (Exception exception)
            {
                failed = true;
                outcome["refreshError"] = exception.Message;
                logger.LogError(exception, "[wishlist.sync] price refresh failed");
            }
        }
        catch (Exception exception)
        {
            // Scope creation itself failed; the host stays up and the next cycle retries.
            failed = true;
            outcome["error"] = exception.Message;
            logger.LogError(exception, "[wishlist.sync] cycle failed");
        }

        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JobRunLog>().FinishAsync(
                jobRunId,
                failed ? JobRunStatuses.Failed : JobRunStatuses.Ok,
                outcome,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // A log write never fails the cycle: the gate still reads the row's started_at.
            logger.LogError(exception, "[wishlist.sync] could not record the run outcome");
        }
    }
}
