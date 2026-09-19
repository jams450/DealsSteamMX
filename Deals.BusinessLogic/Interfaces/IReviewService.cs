using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Per-platform reviews of a canonical game, owned by their author. A game may carry any number of
/// reviews on the same platform: a replay adds a review, it never overwrites the older one. Validation
/// lives here and throws <see cref="ArgumentException"/> (mapped to 400 by the global handler); a review
/// that belongs to another user is reported as not found, never forbidden. Ownership of the game is
/// deliberately not required: the plan does not FK reviews to the import artifact, and a user may review
/// a game they played elsewhere.
/// </summary>
public interface IReviewService
{
    /// <summary>Every review of one canonical game for one user, newest run first.</summary>
    Task<IReadOnlyList<GameReview>> GetForGameAsync(int userId, long gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reviews of a page of canonical games in ONE query, for the library list. Several rows may share a
    /// <c>(gameId, platform)</c>: callers keep the most recent one per pair.
    /// </summary>
    Task<IReadOnlyList<GameReview>> GetForGamesAsync(
        int userId,
        IReadOnlyCollection<long> gameIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reviews of the canonical game reached by an exact <c>('steam', appid)</c> mapping, or an empty list
    /// when there is no canonical identity.
    /// </summary>
    Task<IReadOnlyList<GameReview>> GetForSteamAppAsync(int userId, int appId, CancellationToken cancellationToken = default);

    Task<GameReview> CreateAsync(int userId, GameReviewInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the review does not exist or belongs to another user (404, never 403).</summary>
    Task<GameReview?> UpdateAsync(int userId, long reviewId, GameReviewUpdate input, CancellationToken cancellationToken = default);

    /// <summary>False when the review does not exist or belongs to another user (404, never 403).</summary>
    Task<bool> DeleteAsync(int userId, long reviewId, CancellationToken cancellationToken = default);
}
