using Deals.BusinessLogic.Interfaces;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Periodic wishlist synchronization. Every cycle is isolated in its own scope, and each of the two
/// passes swallows its own failure so a list-sync error never cancels the price refresh (and vice versa),
/// and neither takes the host down.
/// </summary>
public sealed class WishlistSyncJob(
    IServiceScopeFactory scopeFactory,
    IOptions<WishlistOptions> options,
    ILogger<WishlistSyncJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wishlist = options.Value;
        if (!wishlist.Enabled)
        {
            logger.LogInformation("[wishlist.sync] disabled by configuration; job will not run");
            return;
        }

        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, wishlist.StartupDelaySeconds));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(wishlist.TimeZoneId);
        var jitter = TimeSpan.FromMinutes(wishlist.JitterMinutes);

        try
        {
            await Task.Delay(startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Recovery pass: a restart shortly after the scheduled hour must not lose that day's run.
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunPassesAsync(stoppingToken);

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

    private async Task RunPassesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishlistSyncService>();

            try
            {
                var list = await service.SyncListAsync(cancellationToken);
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
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[wishlist.sync] list sync failed");
            }

            try
            {
                var refresh = await service.RefreshWishedGamesAsync(cancellationToken);
                logger.LogInformation(
                    "[wishlist.sync] refresh refreshed={Refreshed} failed={Failed}",
                    refresh.Refreshed,
                    refresh.Failed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[wishlist.sync] price refresh failed");
            }
        }
        catch (Exception exception)
        {
            // Scope creation itself failed; the host stays up and the next cycle retries.
            logger.LogError(exception, "[wishlist.sync] cycle failed");
        }
    }
}
