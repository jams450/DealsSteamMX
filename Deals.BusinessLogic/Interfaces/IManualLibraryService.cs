using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Manual addition of a game (console or any platform) to a user's library, plus the candidate lookup the
/// dialog needs first (<c>docs/PLAN_CONSOLE.md</c> §4/§5). The identity rules live in the implementation:
/// the id is always the canonical <c>games.game_id</c>, and a title match only ever produces candidates
/// for a person to confirm — never a silent write.
/// </summary>
public interface IManualLibraryService
{
    /// <summary>
    /// Catalog games whose normalized title equals the typed one (bounded), each flagged with whether the
    /// user already has a library row for it. Read-only; a suggestion list, not an identity claim.
    /// </summary>
    Task<IReadOnlyList<ManualGameCandidate>> CandidatesAsync(
        int userId,
        string title,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds one <c>owned</c> row for <paramref name="userId"/>. Pass <paramref name="gameId"/> to attach
    /// to an existing canonical game, or <paramref name="create"/> to force a new one; with neither and
    /// matching titles, the write is refused and the result carries the candidates. Returns null when the
    /// user no longer exists (the caller maps it to 404). Throws <see cref="ArgumentException"/> on
    /// invalid input, which the API maps to 400.
    /// </summary>
    /// <param name="igdbId">
    /// Provider row id picked in the search; its cover and year are fetched server-side and applied only
    /// to a row this call creates. A URL never comes from the client (same rule as <c>covers/sync</c>).
    /// </param>
    Task<ManualLibraryAddResult?> AddAsync(
        int userId,
        string? store,
        string? title,
        long? gameId,
        bool create,
        bool? isInstalled,
        DateTime? addedAt,
        long? igdbId,
        CancellationToken cancellationToken = default);
}
