using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Reads the persisted daily rate for a pair, fetching from the primary provider (with fallback)
/// only when no row exists for today. Never called from a user request: only FxRateRefreshJob.
/// </summary>
public interface IFxRateService
{
    Task<FxRate?> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken);

    /// <summary>
    /// Read-only: newest persisted rate at or before today. Never fetches, so it is safe to call
    /// from a user request (the game detail endpoint does exactly this).
    /// </summary>
    Task<FxRate?> GetLatestRateAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken);
}
