using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>Loads the authenticated user's read-only annotations for one Steam AppID.</summary>
public interface ISteamGameUserStateService
{
    Task<SteamGameUserState> GetAsync(int userId, int appId, CancellationToken cancellationToken = default);
}
