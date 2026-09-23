namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Existing catalog row whose normalized title matches the typed one. A suggestion only: identity is
/// never asserted by title, a person confirms the choice (<c>PLAN_CATALOG.md</c> §4).
/// </summary>
public sealed record ManualGameCandidate(long GameId, string Title, int? ReleaseYear, string? ImageUrl, bool InLibrary);

/// <summary>
/// Result of a manual addition (<c>docs/PLAN_CONSOLE.md</c> §4.4). <c>Outcome</c> is one of
/// <see cref="ManualLibraryOutcomes"/>; <c>candidates</c> means the write was refused — the title matches
/// existing games and no choice arrived — so nothing was written and <c>Candidates</c> holds the list to
/// choose from. <c>GameId</c>/<c>UserLibraryId</c> are 0 on that outcome.
/// </summary>
public sealed record ManualLibraryAddResult(
    string Outcome,
    int Created,
    int Attached,
    int DuplicateRow,
    long GameId,
    long UserLibraryId,
    IReadOnlyList<ManualGameCandidate> Candidates);

/// <summary>Wire-visible outcome codes of a manual addition.</summary>
public static class ManualLibraryOutcomes
{
    /// <summary>A new canonical game row was created and the library row attached to it.</summary>
    public const string Created = "created";

    /// <summary>The library row hangs off an existing canonical game the caller chose.</summary>
    public const string Attached = "attached";

    /// <summary>The same (user, store, store_game_id, owned) row already exists: no write, not an error.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Refused: matching titles exist and no gameId/create flag came. Nothing was written.</summary>
    public const string Candidates = "candidates";
}
