namespace Deals.BusinessLogic.Models.Catalog;

/// <summary>One store entry of a duplicate member: the vocabulary of <c>user_library.store</c> plus its id.</summary>
public sealed record DuplicateStoreRef(string Store, string StoreGameId);

/// <summary>
/// One canonical game that shares a folded title with at least one sibling. <see cref="SteamAppIds"/> is
/// the member's own <c>('steam', appid)</c> mappings; <see cref="Blocked"/> marks a member whose identity
/// is already ambiguous (more than one Steam appid on one game), so it can never be a merge target.
/// </summary>
public sealed record DuplicateMember(
    long GameId,
    string Title,
    IReadOnlyList<DuplicateStoreRef> Stores,
    IReadOnlyList<string> SteamAppIds,
    bool Blocked,
    string? BlockReason);

/// <summary>
/// A suggestion group: two or more canonical games in the caller's library whose titles fold to the same
/// key. The fold is a display key only — the merge is always an explicit, manual decision.
/// </summary>
public sealed record DuplicateGroup(string FoldedTitle, IReadOnlyList<DuplicateMember> Members, bool Blocked);

/// <summary>
/// Result of a merge attempt. <see cref="Applied"/> false is a refused merge (409), never an error: the
/// caller decides what to report. A refused merge moves nothing.
/// </summary>
public sealed record GameMergeOutcome(
    bool Applied,
    string? BlockReason,
    int MovedExternalIds,
    int MovedSteamGames,
    int MovedLibraryRows)
{
    public static GameMergeOutcome Blocked(string reason) =>
        new(false, reason, 0, 0, 0);
}
