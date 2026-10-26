using Deals.BusinessLogic.Models.Discovery;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// The manual pending-price pass, as the job executor sees it. It is an interface so the executor wiring and
/// the outcome mapping can be checked offline without a database or a provider.
/// </summary>
public interface ICatalogPriceRefreshService
{
    /// <summary>
    /// Recomputes the pending set <em>of one user's wishlist</em>, refreshes at most
    /// <see cref="CatalogPriceRefreshLimits.BatchSize"/> due games through the existing per-game pipeline and
    /// returns the counters of what actually happened.
    /// </summary>
    /// <param name="requestedByUserId">
    /// The authenticated person who asked for the run, captured from the access token when the job was queued.
    /// It is the only input that decides whose wishlist is read: a non-positive value throws
    /// <see cref="ArgumentException"/> rather than falling back to the whole catalog or to a configured owner.
    /// </param>
    Task<CatalogPriceRefreshOutcome> RefreshPendingAsync(
        int requestedByUserId,
        CancellationToken cancellationToken = default);
}
