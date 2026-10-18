using Deals.BusinessLogic.Models.GgDeals;

namespace Deals.BusinessLogic.Interfaces;

public interface IGgDealsClient
{
    Task<IReadOnlyDictionary<int, GgDealsGamePrice>> GetPricesAsync(IReadOnlyCollection<int> steamAppIds, CancellationToken ct);

    /// <summary>
    /// Reads one page of the documented GG.deals <c>active/</c> catalog. The first call passes a null
    /// cursor; every later call passes the previous page's validated <c>next</c> link. The cursor is
    /// opaque to the caller and is never logged.
    /// </summary>
    Task<GgDealsBundlePage> GetActiveBundlesAsync(string? cursor, CancellationToken ct);
}
