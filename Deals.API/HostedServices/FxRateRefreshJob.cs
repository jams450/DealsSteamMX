using Deals.API.Services.Jobs;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Daily FX refresh. It is the only scheduled caller of the FX executor; user requests never trigger a
/// fetch. Every run is isolated in its own scope and swallows its own failures so the host stays up.
///
/// The cycle is recorded in <c>job_runs</c> and serialized by the same advisory lock the admin dispatcher
/// takes, so a manual refresh cannot overlap a scheduled one. Before this unit the FX job had no lock:
/// with a manual trigger available, two concurrent fetches were suddenly possible.
/// </summary>
public sealed class FxRateRefreshJob(
    IServiceScopeFactory scopeFactory,
    IOptions<FxOptions> options,
    ILogger<FxRateRefreshJob> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fx = options.Value;

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(fx, stoppingToken);

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshAsync(FxOptions fx, CancellationToken cancellationToken)
    {
        await using var lockScope = scopeFactory.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var refreshLock = new PostgresAdvisoryLock(lockDb, FxRateRefreshJobExecutor.AdvisoryLockKey);
        if (!await refreshLock.TryAcquireAsync(cancellationToken))
        {
            logger.LogInformation("[fx.refresh] another process owns the refresh lock; cycle skipped");
            return;
        }

        long jobRunId = 0;
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var jobLog = scope.ServiceProvider.GetRequiredService<JobRunLog>();
            jobRunId = await jobLog.StartAsync(
                JobRunLog.FxRateRefresh,
                JobRunLog.ScheduledTrigger,
                cancellationToken);

            var result = await scope.ServiceProvider.GetRequiredService<FxRateRefreshJobExecutor>()
                .ExecuteAsync(JobRunOptions.Empty, cancellationToken);
            var status = result.Succeeded ? JobRunStatuses.Ok : JobRunStatuses.Failed;

            await jobLog.FinishAsync(jobRunId, status, result.Details, CancellationToken.None);

            if (result.Succeeded)
            {
                logger.LogInformation(
                    "[fx.refresh] {Base}/{Quote} refreshed for the day",
                    fx.BaseCurrency,
                    fx.QuoteCurrency);
            }
            else
            {
                // reason=no_rate lands in the run's details; the log keeps the pair that was asked for.
                logger.LogWarning(
                    "[fx.refresh] no rate returned for {Base}/{Quote}",
                    fx.BaseCurrency,
                    fx.QuoteCurrency);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the run row stays 'running', the honest record of an interrupted cycle.
        }
        catch (Exception exception)
        {
            // Message only: provider exceptions never carry credentials.
            if (jobRunId != 0)
            {
                try
                {
                    using var failureScope = scopeFactory.CreateScope();
                    await failureScope.ServiceProvider.GetRequiredService<JobRunLog>().FinishAsync(
                        jobRunId,
                        JobRunStatuses.Failed,
                        new { reason = "exception" },
                        CancellationToken.None);
                }
                catch (Exception logException)
                {
                    logger.LogWarning(logException, "[fx.refresh] could not record failed run {JobRunId}", jobRunId);
                }
            }

            logger.LogError(exception, "[fx.refresh] failed for {Base}/{Quote}", fx.BaseCurrency, fx.QuoteCurrency);
        }
    }
}
