using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Models.Steam;

/// <summary>Read-only, user-scoped annotations for one canonical Steam game detail.</summary>
public sealed record SteamGameUserState(
    GameOwnership Ownership,
    IReadOnlyList<GameReview> Reviews,
    bool IsFavorite)
{
    public static SteamGameUserState Empty { get; } = new(GameOwnership.None, [], false);
}
