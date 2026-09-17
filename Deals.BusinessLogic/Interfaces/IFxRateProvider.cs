using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Fetches the current daily rate for a currency pair. Implementations throw
/// <see cref="HttpRequestException"/> on non-success responses or malformed payloads,
/// which is what drives the primary/fallback selection in IFxRateService.
/// </summary>
public interface IFxRateProvider
{
    Task<FxRate> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken);
}
