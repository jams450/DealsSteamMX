namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Read-only ownership of one canonical game for the current user, used by the game detail badge.
/// <see cref="OwnedStores"/> is exact identity, <see cref="HasGamePass"/> is a subscription state, and
/// <see cref="PossibleMatchStores"/> is a title candidate that never asserts ownership.
/// </summary>
public sealed record GameOwnership(
    IReadOnlyList<string> OwnedStores,
    bool HasGamePass,
    IReadOnlyList<string> PossibleMatchStores)
{
    /// <summary>No canonical identity, no ownership and no candidate: every field empty.</summary>
    public static readonly GameOwnership None = new([], false, []);
}
