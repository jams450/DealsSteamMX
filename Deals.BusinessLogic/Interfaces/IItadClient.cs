using Deals.BusinessLogic.Models.Itad;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>Scheduling class for a request sharing the local ITAD provider budget.</summary>
public enum ItadRequestPriority
{
    Background,
    Interactive
}

public interface IItadClient
{
    Task<string?> LookupSteamAppIdAsync(
        int appId,
        CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background);
    Task<IReadOnlyList<ItadGamePrices>> GetPricesAsync(
        IReadOnlyCollection<string> itadIds,
        CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background);

    /// <summary>
    /// Active external bundles reported by <c>games/overview/v2</c> for the queried games: the response is
    /// a flat <c>bundles[]</c> list, so the caller attributes each bundle to the game it asked for by
    /// matching the queried id inside <c>tiers[].games[].id</c>. Display metadata only: bundles are never
    /// part of the offer comparison.
    /// </summary>
    Task<IReadOnlyList<ItadBundle>> GetBundlesAsync(
        IReadOnlyCollection<string> itadIds,
        CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background);

    /// <summary>
    /// Follows one ITAD deal link to the store page it redirects to and returns the final URL, or null for
    /// a blank input. This is how a store id is obtained without a title search: the redirect target is the
    /// store's own page, whose path carries the store's id verbatim.
    /// </summary>
    Task<string?> ResolveDealUrlAsync(
        string dealUrl,
        CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background);
}
