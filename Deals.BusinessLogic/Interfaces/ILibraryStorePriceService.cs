using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Store prices for library games that Steam cannot price, written as normal <c>game_offers</c> rows.
///
/// <para>
/// This is a price pass, not an identity pass: an id the catalog already knows is required, and a row whose
/// store id resolves to a different canonical game is skipped instead of priced. The pass never rewrites a
/// library row and never guesses a title.
/// </para>
/// </summary>
public interface ILibraryStorePriceService
{
    /// <summary>
    /// Refreshes the store offer of at most <paramref name="limit"/> of the caller's library rows, skipping
    /// the ones whose offer is already inside the refresh window. Whatever is left comes back as
    /// <see cref="LibraryStorePriceSyncResult.Remaining"/>, so a click is bounded.
    /// </summary>
    Task<LibraryStorePriceSyncResult> SyncStorePricesAsync(
        int userId,
        int limit,
        CancellationToken cancellationToken = default);
}
