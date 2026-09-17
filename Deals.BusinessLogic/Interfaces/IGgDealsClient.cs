using Deals.BusinessLogic.Models.GgDeals;

namespace Deals.BusinessLogic.Interfaces;

public interface IGgDealsClient
{
    Task<IReadOnlyDictionary<int, GgDealsGamePrice>> GetPricesAsync(IReadOnlyCollection<int> steamAppIds, CancellationToken ct);
}
