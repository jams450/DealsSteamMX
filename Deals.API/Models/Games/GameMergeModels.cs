using Deals.BusinessLogic.Models.Catalog;

namespace Deals.API.Models.Games;

/// <summary>
/// Wire contract of the manual merge tool. Mirrors <c>Deals.Web/lib/contracts/games-merge.ts</c> exactly:
/// the frontend validates against these names and types, so a rename here breaks it silently.
/// </summary>
public sealed record GameMergeRequest(long IntoGameId);

/// <summary>One store entry of a duplicate member.</summary>
public sealed record DuplicateStoreRefResponse(string Store, string StoreGameId)
{
    public static DuplicateStoreRefResponse From(DuplicateStoreRef reference) =>
        new(reference.Store, reference.StoreGameId);
}

/// <summary>One canonical game of a duplicate group.</summary>
public sealed record DuplicateMemberResponse(
    long GameId,
    string Title,
    IReadOnlyList<DuplicateStoreRefResponse> Stores,
    IReadOnlyList<string> SteamAppIds,
    bool Blocked,
    string? BlockReason)
{
    public static DuplicateMemberResponse From(DuplicateMember member) => new(
        member.GameId,
        member.Title,
        member.Stores.Select(DuplicateStoreRefResponse.From).ToList(),
        member.SteamAppIds,
        member.Blocked,
        member.BlockReason);
}

/// <summary>A suggestion: two or more canonical games whose titles fold to the same key.</summary>
public sealed record DuplicateGroupResponse(
    string FoldedTitle,
    IReadOnlyList<DuplicateMemberResponse> Members,
    bool Blocked)
{
    public static DuplicateGroupResponse From(DuplicateGroup group) => new(
        group.FoldedTitle,
        group.Members.Select(DuplicateMemberResponse.From).ToList(),
        group.Blocked);
}

/// <summary>
/// Result of a merge. <c>applied</c> false travels with HTTP 409: the merge was refused and nothing moved.
/// </summary>
public sealed record GameMergeOutcomeResponse(
    bool Applied,
    string? BlockReason,
    int MovedExternalIds,
    int MovedSteamGames,
    int MovedLibraryRows)
{
    public static GameMergeOutcomeResponse From(GameMergeOutcome outcome) => new(
        outcome.Applied,
        outcome.BlockReason,
        outcome.MovedExternalIds,
        outcome.MovedSteamGames,
        outcome.MovedLibraryRows);
}
