using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Interfaces;

public interface ISteamGameService
{
    Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<SteamGameDetails?> GetByAppIdAsync(int appId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SteamSearchResult>> GetSuggestionsAsync(string? query, CancellationToken cancellationToken);
}
