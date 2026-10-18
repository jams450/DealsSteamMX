using Deals.BusinessLogic.Models.Discovery;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Global ITAD deals discovery (<c>deals/v2</c>), explicitly authorized by spec S4. It is deliberately
/// separate from <see cref="IItadClient"/>: the per-game client is identity and offer driven, while
/// this one is a paginated global catalog for the deals feed. It shares the existing ITAD key header
/// and the same <c>ItadRequestGovernor</c> singleton, so it cannot introduce a second budget.
/// </summary>
public interface IGlobalDealClient
{
    /// <summary>
    /// One page of the global deals catalog. The provider cap for <paramref name="limit"/> is 200 and a
    /// larger value is clamped client-side. The response must be an object with a <c>list</c> array and
    /// a boolean <c>hasMore</c>; a malformed envelope or a structurally invalid deal throws instead of
    /// yielding a short list, because a partial page must never be certified as a complete snapshot and
    /// purge the previous one.
    /// </summary>
    Task<GlobalDealPage> GetDealsPageAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background);
}
