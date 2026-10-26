using Deals.BusinessLogic.Models.Discovery;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Resolves the price universe of one authenticated user (spec S4/S5): their wishlist, expressed in canonical
/// <c>game_id</c>s.
///
/// It is a required dependency of the monitor and of the manual price pass, so neither has an unscoped path and
/// neither can fall back to the whole catalog or to a configured operator. The user id always comes from the
/// server-side identity (the access token for the monitor, the queue item captured from that token for the
/// job) — never from a request body, a query string or an ambient <c>HttpContext</c>.
/// </summary>
public interface ICatalogWishlistScopeResolver
{
    /// <summary>
    /// The canonical games of <paramref name="userId"/>'s wishlist. An empty scope is a valid answer (the user
    /// has no wished game); a non-positive id throws <see cref="ArgumentException"/> instead of defaulting.
    /// </summary>
    Task<CatalogWishlistScope> LoadAsync(int userId, CancellationToken cancellationToken = default);
}
