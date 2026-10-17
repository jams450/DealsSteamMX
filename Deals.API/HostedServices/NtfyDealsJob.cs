using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Services;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Bounded polling job for the global ntfy deal alerts. Each cycle runs two read-only-over-providers
/// phases inside one scope: the producer reads fresh persisted snapshots and fills the outbox, the
/// dispatcher delivers due rows. It never refreshes a price provider and never calls a provider.
///
/// Concurrency follows <see cref="WishlistSyncJob"/>: a PostgreSQL advisory lock serializes cycles across
/// processes, so two hosts never enqueue or send the same deal at once. A cycle failure is swallowed and
/// never takes the host down.
/// </summary>
public sealed class NtfyDealsJob(
    IServiceScopeFactory scopeFactory,
    IOptions<NtfyOptions> options,
    ILogger<NtfyDealsJob> logger) : BackgroundService
{
    // Distinct from the wishlist sync lock (7_831_442_091) and the per-user category locks.
    private const long LockKey = 7_831_442_092L;

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

        await using var scope = scopeFactory.CreateAsyncScope();
        var producer = scope.ServiceProvider.GetRequiredService<DealAlertProducer>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<DealAlertDispatcher>();

        var produce = await producer.ProduceAsync(cancellationToken);
        var dispatch = await dispatcher.DispatchAsync(cancellationToken);

        logger.LogInformation(
            "[ntfy] cycle candidates={Candidates} triggered={Triggered} enqueued={Enqueued} claimed={Claimed} sent={Sent} retried={Retried} failed={Failed} stale={Stale}",
            produce.Candidates,
            produce.Triggered,
            produce.Enqueued,
            dispatch.Claimed,
            dispatch.Sent,
            dispatch.Retried,
            dispatch.Failed,
            dispatch.Stale);
    }
}
