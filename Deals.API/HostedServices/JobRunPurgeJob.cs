using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>Daily bounded purge of completed job history. Running rows are never deleted.</summary>
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
        var cutoff = DateTime.UtcNow.AddDays(-options.Value.RetentionDays);
        var batchSize = options.Value.PurgeBatchSize;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
            var deleted = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var rows = await repository.GetTrack<JobRun>()
                    .Where(run => run.Status != JobRunStatuses.Running && run.FinishedAt < cutoff)
                    .OrderBy(run => run.JobRunId)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);
                if (rows.Count == 0) break;
                repository.GetTrack<JobRun>().RemoveRange(rows);
                await repository.SaveChangesAsync();
                deleted += rows.Count;
            }
            logger.LogInformation("[jobs.purge] deleted={Deleted} retentionDays={RetentionDays}", deleted, options.Value.RetentionDays);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "[jobs.purge] failed without affecting running jobs");
        }
    }
}
