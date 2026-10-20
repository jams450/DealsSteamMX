using Deals.API.Services.Jobs;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Bounded polling job for the global ntfy deal alerts. Each cycle runs two phases inside one scope: the
/// producer reads fresh persisted snapshots and fills the outbox, the dispatcher delivers due rows. It never
/// refreshes a price provider and never calls a provider.
///
/// Concurrency follows <see cref="WishlistSyncJob"/>: a PostgreSQL advisory lock serializes cycles across
/// processes, so two hosts never enqueue or send the same deal at once. The admin dispatcher takes the same
/// key, so a manual cycle cannot overlap a polling one. A cycle failure is swallowed and never takes the
/// host down.
///
/// Every cycle is recorded in <c>job_runs</c> now that the admin page reports this job. Details carry only
/// counters: an outbox error message could embed the ntfy base URL or the topic.
/// </summary>
public sealed class NtfyDealsJob(
    IServiceScopeFactory scopeFactory,
    IOptions<NtfyOptions> options,
    ILogger<NtfyDealsJob> logger) : BackgroundService
{
    private const long LockKey = NtfyDealsJobExecutor.AdvisoryLockKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("[ntfy] alerts disabled by configuration; job will not run");
            return;
        }

        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, settings.StartupDelaySeconds));
        var interval = TimeSpan.FromSeconds(Math.Max(5, settings.PollSeconds));

        try
        {
            await Task.Delay(startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // Deliberately only the type name: an unhandled HttpRequestException message can embed the
                // request URL, and the outbox logs must never carry the base URL or the topic.
                logger.LogError("[ntfy] cycle failed errorType={ErrorType}", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var lockScope = scopeFactory.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var cycleLock = new PostgresAdvisoryLock(lockDb, LockKey);
        if (!await cycleLock.TryAcquireAsync(cancellationToken))
        {
            logger.LogInformation("[ntfy] another process owns the alert lock; cycle skipped");
            return;
        }

        var jobRunId = 0L;
        try
        {
            jobRunId = await lockScope.ServiceProvider.GetRequiredService<JobRunLog>()
                .StartAsync(JobKeys.NtfyAlerts, JobRunLog.ScheduledTrigger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail open: an unwritable log must never stop the alerts.
            logger.LogError("[ntfy] could not record the cycle errorType={ErrorType}", exception.GetType().Name);
        }

        var status = JobRunStatuses.Ok;
        object? details = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<NtfyDealsJobExecutor>()
                .ExecuteAsync(NtfyDealsJobExecutor.ScheduledOptions, cancellationToken);
            status = result.Succeeded ? JobRunStatuses.Ok : JobRunStatuses.Failed;
            details = result.Details;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the row stays 'running', the honest record of an interrupted cycle.
            return;
        }
        catch (Exception exception)
        {
            status = JobRunStatuses.Failed;
            details = new { errorType = exception.GetType().Name };
            logger.LogError("[ntfy] cycle failed errorType={ErrorType}", exception.GetType().Name);
        }

        try
        {
            await lockScope.ServiceProvider.GetRequiredService<JobRunLog>()
                .FinishAsync(jobRunId, status, details, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError("[ntfy] could not record the cycle outcome errorType={ErrorType}", exception.GetType().Name);
        }
    }
}
