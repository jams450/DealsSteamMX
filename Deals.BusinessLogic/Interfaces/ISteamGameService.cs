using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;

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

    /// <summary>
    /// The manual pending-price pass for one game: every due price phase — including the Steam direct snapshot —
    /// gated by the <b>chosen</b> window (spec S2/S3) instead of the configured provider TTL. Same per-app gate, same
    /// governors, same "a failed answer never advances a timestamp" rule as <see cref="RefreshProvidersAsync"/>, and
    /// nothing is ever forced.
    ///
    /// <paramref name="ageHours"/> is the requester's window, bounded by
    /// <see cref="Deals.BusinessLogic.Models.Discovery.CatalogPriceRefreshLimits"/>; it is a parameter and never a
    /// configuration read, so one run cannot inherit the 7-day provider TTL or a fixed 72 h guard per game. An
    /// out-of-range window throws before any provider is called.
    ///
    /// It returns a typed <see cref="SteamStalePriceRefresh"/> instead of only the details because the pipeline
    /// absorbs a provider fault into a <c>Failed</c> phase result: without which phases were due, which really
    /// answered and which failed, the caller cannot tell a completed price from a failed call.
    /// </summary>
    Task<SteamStalePriceRefresh> RefreshStalePricesAsync(
        int appId,
        CancellationToken cancellationToken,
        int ageHours = GameRefreshPolicy.StalePriceBatchHours);

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
