using Deals.API.Services.Jobs;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Daily bounded purge of completed job history. Running rows are never deleted.
///
/// The cycle is recorded in <c>job_runs</c> and serialized by the same advisory lock the admin dispatcher
/// takes, so a manual purge cannot race this one. Its own row is written first: a <c>running</c> row is
/// never a deletion candidate, so the purge cannot erase the record of what it is doing.
/// </summary>
public sealed class JobRunPurgeJob(
    IServiceScopeFactory scopeFactory,
    IOptions<JobBudgetOptions> options,
    ILogger<JobRunPurgeJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PurgeAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromDays(1), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        await using var lockScope = scopeFactory.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var purgeLock = new PostgresAdvisoryLock(lockDb, JobRunPurgeJobExecutor.AdvisoryLockKey);
        if (!await purgeLock.TryAcquireAsync(cancellationToken))
        {
            logger.LogInformation("[jobs.purge] another process owns the purge lock; cycle skipped");
            return;
        }

        var jobRunId = 0L;
        try
        {
            jobRunId = await lockScope.ServiceProvider.GetRequiredService<JobRunLog>()
                .StartAsync(JobKeys.Purge, JobRunLog.ScheduledTrigger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[jobs.purge] could not record the run; continuing without a log entry");
        }

        var status = JobRunStatuses.Ok;
        object? details = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<JobRunPurgeJobExecutor>()
                .ExecuteAsync(JobRunOptions.Empty, cancellationToken);
            status = result.Succeeded ? JobRunStatuses.Ok : JobRunStatuses.Failed;
            details = result.Details;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the row stays 'running', the honest record of an interrupted purge.
            return;
        }
        catch (Exception exception)
        {
            status = JobRunStatuses.Failed;
            details = new { errorType = exception.GetType().Name };
            logger.LogError(exception, "[jobs.purge] failed without affecting running jobs");
        }

        try
        {
            await lockScope.ServiceProvider.GetRequiredService<JobRunLog>()
                .FinishAsync(jobRunId, status, details, cancellationToken);
            logger.LogInformation(
                "[jobs.purge] cycle finished status={Status} retentionDays={RetentionDays}",
                status,
                options.Value.RetentionDays);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[jobs.purge] could not record the run outcome");
        }
    }
}
