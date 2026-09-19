using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Favorite games of one user. A favorite is a per-user mark on a canonical game, independent of the
/// library import and of any review: a game can be favorited without a review, and a review does not make
/// a favorite. Unmarking is idempotent; validating that the game exists is the service's job (the FK would
/// otherwise surface as a 500).
/// </summary>
public interface IFavoriteService
{
    /// <summary>
    /// The subset of <paramref name="gameIds"/> the user has favorited, in ONE query, for the library page.
    /// </summary>
    Task<IReadOnlyList<long>> GetFavoriteGameIdsAsync(
        int userId,
        IReadOnlyCollection<long> gameIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the canonical game reached by an exact <c>('steam', appid)</c> mapping is a favorite, or
    /// false when there is no canonical identity.
    /// </summary>
    Task<bool> IsFavoriteForSteamAppAsync(int userId, int appId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks or unmarks a favorite by canonical game id. Returns false when the game does not exist in the
    /// catalog (the caller answers 400, never a 500).
    /// </summary>
    Task<bool> SetByGameIdAsync(int userId, long gameId, bool favorite, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="SetByGameIdAsync"/> but reached by Steam appid. Returns false when the appid has no
    /// canonical game.
    /// </summary>
    Task<bool> SetBySteamAppIdAsync(int userId, int appId, bool favorite, CancellationToken cancellationToken = default);
}
