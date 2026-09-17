using Deals.BusinessLogic.Models.Itad;

namespace Deals.BusinessLogic.Interfaces;

public interface IItadClient
{
    Task<string?> LookupSteamAppIdAsync(int appId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ItadGamePrices>> GetPricesAsync(IReadOnlyCollection<string> itadIds, CancellationToken cancellationToken);
}
