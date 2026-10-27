using Deals.BusinessLogic.Models.Discovery;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// The manual pending-price pass, as the job executor sees it. It is an interface so the executor wiring and
/// the outcome mapping can be checked offline without a database or a provider.
/// </summary>
public interface ICatalogPriceRefreshService
{
    /// <summary>
    /// Recomputes the pending set <em>of one user's wishlist</em> for the chosen window, refreshes at most
    /// <see cref="CatalogPriceRefreshLimits.BatchSize"/> due games through the existing per-game pipeline and
    /// returns the counters of what actually happened.
    /// </summary>
    /// <param name="requestedByUserId">
    /// The authenticated person who asked for the run, captured from the access token when the job was queued.
    /// It is the only input that decides whose wishlist is read: a non-positive value throws
    /// <see cref="ArgumentException"/> rather than falling back to the whole catalog or to a configured owner.
    /// </param>
    /// <param name="ageHours">
    /// The window the requester chose (spec S2/S3): a price older than this many hours is pending, and so is a game
    /// with no price at all. Bounded by <see cref="CatalogPriceRefreshLimits"/> (1..720, default 72) and used for the
    /// bucket, the provider gates, the selection, the measurement and the remaining count. It is a parameter and never
    /// a configuration read, and an out-of-range value throws before any provider call.
    /// </param>
    Task<CatalogPriceRefreshOutcome> RefreshPendingAsync(
        int requestedByUserId,
        CancellationToken cancellationToken = default,
        int ageHours = CatalogPriceRefreshLimits.DefaultAgeHours);
}
