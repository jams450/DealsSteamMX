using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Interfaces;

public interface ISteamGameService
{
    Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<SteamGameDetails?> GetByAppIdAsync(
        int appId,
        bool forceRefresh,
        CancellationToken cancellationToken,
        bool interactive = false);

    /// <summary>
    /// Steam-only detail load: fetches and persists the store snapshot without touching ITAD, gg.deals,
    /// bundles or any refresh timestamp. Seeds a <c>steam_games</c> row for a game that has never been
    /// opened, so the price comparators have something to read later.
    /// </summary>
    Task<SteamGameDetails?> GetAppDetailsOnlyAsync(int appId, CancellationToken cancellationToken);

    /// <summary>Due external providers only, using persisted Steam details; never requests Steam.</summary>
    Task<SteamGameDetails?> RefreshProvidersAsync(int appId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SteamSearchResult>> GetSuggestionsAsync(string? query, CancellationToken cancellationToken);

    /// <summary>
    /// Read-only discovery lists over persisted <c>steam_games</c> rows (region MX with a current
    /// price). <c>list</c> is one of <c>discount</c> (by discount desc), <c>historic</c> (current
    /// price at the local low) or <c>recent</c> (by observation date desc). Throws
    /// <see cref="ArgumentException"/> for an unknown list.
    /// </summary>
    Task<IReadOnlyList<SteamDiscoverItem>> GetDiscoverAsync(
        string list,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
