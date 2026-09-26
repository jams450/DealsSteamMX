using Deals.BusinessLogic.Models.Catalog;

namespace Deals.BusinessLogic.Interfaces;

public interface ICrossStateReconciliationService
{
    Task<IReadOnlyList<CrossStateCandidateGroup>> FindCandidatesAsync(
        int userId,
        CancellationToken cancellationToken = default);
}
