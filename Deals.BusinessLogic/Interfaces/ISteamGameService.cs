using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Interfaces;

public interface ISteamGameService
{
    Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<SteamGameDetails?> GetByAppIdAsync(int appId, bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>
    /// Steam-only detail load: fetches and persists the store snapshot without touching ITAD, gg.deals,
    /// bundles or any refresh timestamp. Seeds a <c>steam_games</c> row for a game that has never been
    /// opened, so the price comparators have something to read later.
    /// </summary>
    Task<SteamGameDetails?> GetAppDetailsOnlyAsync(int appId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SteamSearchResult>> GetSuggestionsAsync(string? query, CancellationToken cancellationToken);
}
