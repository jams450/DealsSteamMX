using Deals.BusinessLogic.Models.Catalog;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Manual fusion of duplicate canonical games. Identity is the exact <c>(namespace, external_id)</c> pair,
/// so a merge is only ever an explicit decision: it repoints every referrer of the absorbed game to the
/// survivor and then deletes the absorbed row. There is no automatic or title-based merge anywhere.
/// </summary>
public interface IGameMergeService
{
    /// <summary>
    /// Candidate duplicates for one user: canonical games in that user's library whose title folds to the
    /// same key. Read-only, suggestion only.
    /// </summary>
    Task<IReadOnlyList<DuplicateGroup>> FindDuplicateGroupsAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges <paramref name="absorbedGameId"/> into <paramref name="survivorGameId"/> in one transaction.
    /// Returns <see cref="GameMergeOutcome.Blocked"/> with a reason instead of throwing when the merge is
    /// refused (ambiguous Steam identity). Reviews are never dropped: they all move to the survivor.
    /// Throws when a game id does not exist.
    /// </summary>
    Task<GameMergeOutcome> MergeAsync(
        long absorbedGameId,
        long survivorGameId,
        int actorUserId,
        string actorName,
        CancellationToken cancellationToken = default);
}
