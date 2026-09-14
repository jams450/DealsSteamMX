using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Interfaces;

public interface ISteamStoreClient
{
    Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<SteamGameDetails?> GetAppDetailsAsync(int appId, CancellationToken cancellationToken);
}
