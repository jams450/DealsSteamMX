using Deals.BusinessLogic.Interfaces;
using Microsoft.Extensions.Options;

namespace Deals.API.HostedServices;

/// <summary>
/// Daily FX refresh. It is the only caller of IFxRateService: user requests never trigger a fetch.
/// Every run is isolated in its own scope and swallows its own failures so the host stays up.
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
            await RefreshAsync(fx.BaseCurrency, fx.QuoteCurrency, stoppingToken);

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

    private async Task RefreshAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IFxRateService>();
            var rate = await service.GetRateAsync(baseCurrency, quoteCurrency, cancellationToken);

            if (rate == null)
            {
                logger.LogWarning("[fx.refresh] no rate returned for {Base}/{Quote}", baseCurrency, quoteCurrency);
                return;
            }

            logger.LogInformation(
                "[fx.refresh] {Base}/{Quote} {RateDate} from {Source}",
                rate.Base,
                rate.Quote,
                rate.RateDate,
                rate.Source);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // Message only: provider exceptions never carry credentials.
            logger.LogError(ex, "[fx.refresh] failed for {Base}/{Quote}", baseCurrency, quoteCurrency);
        }
    }
}
