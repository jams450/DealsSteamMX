using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Deals.API.Services.Jobs;

// The work of each registered job, in one place. Before the manual dispatcher existed this logic lived
// inside the hosted services' private RunOnce methods, so a manual run would have had to be a second
// implementation with its own counters and its own bugs. Each executor now owns the cycle and its details
// shape; the scheduled hosted service and JobRunDispatchService both call it, and differ only in who takes
// the advisory lock and which trigger they write to job_runs.

/// <summary>
/// <c>wishlist.sync</c>. Two independent phases: importing the Steam list, and the per-game price refresh.
/// The price phase is the expensive one and is off unless asked for, which is why a manual run can validate
/// the job without spending tens of minutes.
/// </summary>
public sealed class WishlistSyncJobExecutor(
    IWishlistSyncService syncService,
    // Scoped observer, claimed by ExecuteAsync and never by a request handler.
    ISyncDiagnosticsCollector diagnostics,
    ILogger<WishlistSyncJobExecutor> logger) : IJobExecutor
{
    public const string JobKey = JobKeys.WishlistSync;

    /// <summary>Same lock the weekly scheduler and <c>POST /api/wishlist/sync</c> use.</summary>
    public const long AdvisoryLockKey = 7_831_442_091L;

    /// <summary>
    /// What the scheduled cycle runs: the list and the price pass, exactly as before the catalog existed.
    /// The manual default is the opposite (list only), so this is spelled out rather than inherited.
    /// </summary>
    public static JobRunOptions ScheduledOptions { get; } = new(
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JobOptionKeys.IncludePriceRefresh] = true
        });

    public string Job => JobKey;

    public async Task<JobExecutionResult> ExecuteAsync(JobRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // This cycle owns the diagnosis of this scope (spec S2/S3). Claiming it here — before any phase — is what makes
        // the collector job-only: an interactive request's scope is never claimed and therefore records nothing.
        diagnostics.Activate();

        var outcome = new Dictionary<string, object?>(StringComparer.Ordinal);
        var failed = false;

        // Each phase swallows its own failure so a list-sync error never cancels the price refresh.
        try
        {
            var list = await syncService.SyncListAsync(cancellationToken);
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
            throw;
        }
        catch (Exception exception)
        {
            failed = true;
            outcome["listError"] = exception.Message;
        }

        if (options.IncludePriceRefresh)
        {
            try
            {
                var refresh = await syncService.RefreshWishedGamesAsync(cancellationToken);
                outcome["refresh"] = new { refresh.Refreshed, refresh.Failed, refresh.Providers };
                logger.LogInformation(
                    "[wishlist.sync] refresh refreshed={Refreshed} failed={Failed} providers={@Providers}",
                    refresh.Refreshed,
                    refresh.Failed,
                    refresh.Providers);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed = true;
                outcome["refreshError"] = exception.Message;
            }
        }
        else
        {
            outcome["refresh"] = new { requested = false, reason = "price_refresh_not_requested" };
        }

        // The safe diagnosis of this cycle (spec S2/S3), attached at the root of the executor's details: a
        // scheduled run writes this object directly to job_runs.details, and a manual run reaches it under
        // details.result. It is skipped when no provider phase ran, so a run that touched nothing does not claim
        // a report it cannot fill. Nothing above reads it: the cycle's behaviour is unchanged.
        if (diagnostics.HasEvents)
        {
            outcome["diagnostics"] = diagnostics.BuildSnapshot();
        }

        return new JobExecutionResult(!failed, outcome);
    }
}

/// <summary><c>fx.rate-refresh</c>. The only caller of <see cref="IFxRateService"/>; user requests never fetch.</summary>
public sealed class FxRateRefreshJobExecutor(
    IFxRateService fxRateService,
    IOptions<FxOptions> options) : IJobExecutor
{
    public const string JobKey = JobKeys.FxRateRefresh;

    /// <summary>The FX job had no lock before the dispatcher; a manual run needs one to avoid a double fetch.</summary>
    public const long AdvisoryLockKey = 7_831_442_096L;

    public string Job => JobKey;

    public async Task<JobExecutionResult> ExecuteAsync(JobRunOptions _, CancellationToken cancellationToken)
    {
        var fx = options.Value;
        var rate = await fxRateService.GetRateAsync(fx.BaseCurrency, fx.QuoteCurrency, cancellationToken);
        if (rate is null)
        {
            return new JobExecutionResult(false, new
            {
                reason = "no_rate",
                fx.BaseCurrency,
                fx.QuoteCurrency
            });
        }

        return new JobExecutionResult(true, new { rate.Base, rate.Quote, rate.RateDate, rate.Source });
    }
}

/// <summary><c>bundles.global-sync</c>. A sweep that truncates preserves the previous snapshot, and that is recorded as failed.</summary>
public sealed class GlobalBundleSyncJobExecutor(GlobalBundleSyncService syncService) : IJobExecutor
{
    public const string JobKey = JobKeys.GlobalBundles;

    public string Job => JobKey;

    public async Task<JobExecutionResult> ExecuteAsync(JobRunOptions _, CancellationToken cancellationToken)
    {
        var result = await syncService.SyncAsync(cancellationToken);
        return new JobExecutionResult(result.Completed, new
        {
            result.Completed,
            Reason = result.IncompleteReason.ToString(),
            result.Pages,
            result.Bundles,
            result.Written,
            result.Removed
        });
    }
}

/// <summary><c>bundles.ggdeals-sync</c>. Same shape as the global sweep: a truncated catalog is not written.</summary>
public sealed class GgDealsBundleSyncJobExecutor(GgDealsBundleSyncService syncService) : IJobExecutor
{
    public const string JobKey = JobKeys.GgDealsBundles;

    public string Job => JobKey;

    public async Task<JobExecutionResult> ExecuteAsync(JobRunOptions _, CancellationToken cancellationToken)
    {
        var result = await syncService.SyncAsync(cancellationToken);
        return new JobExecutionResult(result.Completed, new
        {
            result.Completed,
            Reason = result.IncompleteReason.ToString(),
            result.Pages,
            result.Bundles,
            result.Written,
            result.Removed
        });
    }
}

/// <summary><c>deals.global-sync</c>. A truncated feed preserves the previous snapshot and is recorded as failed.</summary>
public sealed class GlobalDealSyncJobExecutor(GlobalDealSyncService syncService) : IJobExecutor
{
    public const string JobKey = JobKeys.GlobalDeals;

    public string Job => JobKey;

    public async Task<JobExecutionResult> ExecuteAsync(JobRunOptions _, CancellationToken cancellationToken)
    {
        var result = await syncService.SyncAsync(cancellationToken);
        return new JobExecutionResult(result.Completed, new
        {
            result.Completed,
            Reason = result.IncompleteReason.ToString(),
            result.Pages,
            result.Deals,
            result.Written,
            result.Removed
        });
    }
}

/// <summary>
/// <c>ntfy.alerts</c>. Both phases are read-only over providers: the producer reads persisted snapshots, the
/// dispatcher delivers due outbox rows. The two switches exist because sending is the only phase with an
/// external, human-visible side effect, and an operator validating the job may want the diagnosis without it.
/// </summary>
public sealed class NtfyDealsJobExecutor(
    DealAlertProducer producer,
    DealAlertDispatcher dispatcher,
    ILogger<NtfyDealsJobExecutor> logger) : IJobExecutor
{
    public const string JobKey = JobKeys.NtfyAlerts;

    /// <summary>Distinct from the wishlist sync lock (7_831_442_091) and the per-user category locks.</summary>
    public const long AdvisoryLockKey = 7_831_442_092L;

    /// <summary>
    /// What the polling cycle runs: both phases, as before the switches existed. Spelled out because an
    /// empty <see cref="JobRunOptions"/> means "no phase selected", which is a refusal, not a default.
    /// </summary>
    public static JobRunOptions ScheduledOptions { get; } = new(
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JobOptionKeys.Produce] = true,
            [JobOptionKeys.Dispatch] = true
        });

    public string Job => JobKey;

    public async Task<JobExecutionResult> ExecuteAsync(JobRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.ProduceAlerts && !options.DispatchAlerts)
        {
            // Neither phase selected is not a successful cycle: it did nothing at all.
            return new JobExecutionResult(false, new { reason = "no_phase_selected" });
        }

        int? candidates = null;
        int? triggered = null;
        int? enqueued = null;
        int? claimed = null;
        int? sent = null;
        int? retried = null;
        int? failed = null;
        int? stale = null;

        if (options.ProduceAlerts)
        {
            var produce = await producer.ProduceAsync(cancellationToken);
            candidates = produce.Candidates;
            triggered = produce.Triggered;
            enqueued = produce.Enqueued;
        }

        if (options.DispatchAlerts)
        {
            var dispatch = await dispatcher.DispatchAsync(cancellationToken);
            claimed = dispatch.Claimed;
            sent = dispatch.Sent;
            retried = dispatch.Retried;
            failed = dispatch.Failed;
            stale = dispatch.Stale;
        }

        // Deliberately only counters: an outbox error message can embed the base URL or the topic.
        logger.LogInformation(
            "[ntfy] cycle candidates={Candidates} triggered={Triggered} enqueued={Enqueued} claimed={Claimed} sent={Sent} retried={Retried} failed={Failed} stale={Stale}",
            candidates,
            triggered,
            enqueued,
            claimed,
            sent,
            retried,
            failed,
            stale);

        // A cycle that produced no alert is not a failure: there was simply nothing to say.
        return new JobExecutionResult(true, new
        {
            produced = options.ProduceAlerts,
            dispatched = options.DispatchAlerts,
            candidates,
            triggered,
            enqueued,
            claimed,
            sent,
            retried,
            failed,
            stale
        });
    }
}

/// <summary>
/// <c>jobs.purge</c>. Deletes finished runs older than the retention window in bounded batches and never
/// touches a row that is still <c>running</c>. It is the only destructive job in the catalog, which is why
/// it is the only one that can be asked for a dry run and the only one that requires a confirmation.
/// </summary>
public sealed class JobRunPurgeJobExecutor(
    IRepository repository,
    IOptions<JobBudgetOptions> budgetOptions) : IJobExecutor
{
    public const string JobKey = JobKeys.Purge;

    /// <summary>The purge is new to the lock table: a manual purge must not race the daily one.</summary>
    public const long AdvisoryLockKey = 7_831_442_097L;

    public string Job => JobKey;

    public async Task<JobExecutionResult> ExecuteAsync(JobRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var budget = budgetOptions.Value;
        var cutoff = DateTime.UtcNow.AddDays(-budget.RetentionDays);
        var batchSize = budget.PurgeBatchSize;

        if (options.DryRun)
        {
            // Read-only: counts what the real purge would remove, deletes nothing.
            var wouldDelete = await repository.Get<JobRun>()
                .CountAsync(
                    run => run.Status != JobRunStatuses.Running && run.FinishedAt < cutoff,
                    cancellationToken);
            return new JobExecutionResult(true, new
            {
                dryRun = true,
                deleted = 0,
                wouldDelete,
                retentionDays = budget.RetentionDays,
                cutoff
            });
        }

        var deleted = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var rows = await repository.GetTrack<JobRun>()
                .Where(run => run.Status != JobRunStatuses.Running && run.FinishedAt < cutoff)
                .OrderBy(run => run.JobRunId)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            repository.GetTrack<JobRun>().RemoveRange(rows);
            await repository.SaveChangesAsync();
            deleted += rows.Count;
        }

        return new JobExecutionResult(true, new
        {
            dryRun = false,
            deleted,
            retentionDays = budget.RetentionDays,
            cutoff
        });
    }
}
