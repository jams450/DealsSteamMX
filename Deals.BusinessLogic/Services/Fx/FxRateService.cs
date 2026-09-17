using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services.Fx;

public sealed class FxRateService(
    IRepository repository,
    BanxicoFxRateProvider banxicoProvider,
    FrankfurterFxRateProvider frankfurterProvider) : IFxRateService
{
    public async Task<FxRate?> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken)
    {
        var baseCode = NormalizeCurrency(baseCurrency, nameof(baseCurrency));
        var quoteCode = NormalizeCurrency(quoteCurrency, nameof(quoteCurrency));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Only an exact hit for today satisfies the daily refresh. A persisted rate from an earlier
        // date (weekend, holiday, or a stale backfill) must not stop today's fetch.
        var todayRate = await repository.Get<FxRate>()
            .FirstOrDefaultAsync(
                rate => rate.Base == baseCode && rate.Quote == quoteCode && rate.RateDate == today,
                cancellationToken);

        if (todayRate != null)
        {
            return todayRate;
        }

        FxRate fetched;
        try
        {
            fetched = await banxicoProvider.GetRateAsync(baseCode, quoteCode, cancellationToken);
        }
        catch (HttpRequestException)
        {
            fetched = await frankfurterProvider.GetRateAsync(baseCode, quoteCode, cancellationToken);
        }

        // A provider may report a past business date (Frankfurter lags); UpsertAsync updates that row.
        return await UpsertAsync(fetched, cancellationToken);
    }

    public async Task<FxRate?> GetLatestRateAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken)
    {
        var baseCode = NormalizeCurrency(baseCurrency, nameof(baseCurrency));
        var quoteCode = NormalizeCurrency(quoteCurrency, nameof(quoteCurrency));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Read-only by contract: no provider fallback here, ever.
        return await repository.Get<FxRate>()
            .Where(rate => rate.Base == baseCode && rate.Quote == quoteCode && rate.RateDate <= today)
            .OrderByDescending(rate => rate.RateDate)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<FxRate> UpsertAsync(FxRate fetched, CancellationToken cancellationToken)
    {
        var existing = await repository.GetTrack<FxRate>()
            .FirstOrDefaultAsync(
                rate => rate.Base == fetched.Base && rate.Quote == fetched.Quote && rate.RateDate == fetched.RateDate,
                cancellationToken);

        if (existing == null)
        {
            return await repository.Save(fetched);
        }

        existing.Rate = fetched.Rate;
        existing.Source = fetched.Source;
        existing.FetchedAt = fetched.FetchedAt;
        await repository.SaveChangesAsync();
        return existing;
    }

    private static string NormalizeCurrency(string currency, string parameterName)
    {
        var code = currency?.Trim().ToUpperInvariant();
        if (code is null || code.Length != 3 || !code.All(char.IsAsciiLetter))
        {
            throw new ArgumentException("Currency code must be three letters.", parameterName);
        }

        return code;
    }
}
