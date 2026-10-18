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
    /// One page of the global ITAD bundle catalog (<c>GET bundles/v1</c>). Unlike
    /// <see cref="GetBundlesAsync"/>, the response is a root array with no queried-id set: every item id
    /// found inside the tiers is reported as observed, so the caller can match wishlist UUIDs locally.
    /// The page size is capped at 50 by the provider. A malformed envelope or a structurally invalid
    /// bundle (for example a missing <c>url</c>) throws instead of yielding a short list, because a partial
    /// page must never be certified as a complete snapshot and purge the previous one.
    /// </summary>
    Task<IReadOnlyList<ItadBundle>> GetGlobalBundlesPageAsync(
        int offset,
        int limit,
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
