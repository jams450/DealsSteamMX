using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Keyless Steam wishlist reader. There is no API key: the endpoint is registered in Valve's API list
/// but undocumented, so it is isolated behind this interface.
/// </summary>
public interface ISteamWishlistClient
{
    Task<SteamWishlistResult> GetWishlistAsync(string steamId64, CancellationToken cancellationToken);
}
