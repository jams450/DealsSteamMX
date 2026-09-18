using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Resolves, strictly read-only, whether the current user owns one game reached by its Steam appid.
/// It never creates canonical games, never inserts external ids and never writes <c>game_id</c>.
/// </summary>
public interface IGameOwnershipService
{
    Task<GameOwnership> ResolveAsync(
        int appId,
        int userId,
        CancellationToken cancellationToken = default);
}
