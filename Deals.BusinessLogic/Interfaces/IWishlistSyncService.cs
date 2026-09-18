using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Two deliberately separate operations: the list snapshot is fast enough to run inside an HTTP request,
/// while the per-game price refresh is paced over tens of minutes and only the background job runs it.
/// </summary>
public interface IWishlistSyncService
{
    /// <summary>Imports each linked user's wishlist into <c>user_library</c> and prunes removed entries.</summary>
    Task<WishlistListSyncReport> SyncListAsync(CancellationToken cancellationToken);

    /// <summary>Prices the wished games through the shared Steam/ITAD/gg.deals pipeline, paced.</summary>
    Task<WishlistRefreshReport> RefreshWishedGamesAsync(CancellationToken cancellationToken);
}
