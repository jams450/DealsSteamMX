namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// One game related to a bundle through <c>external_bundle_games</c>. The relation is the actual item
/// identity ITAD matched when the bundle was observed, so a linked game is a confirmed member of the
/// bundle; which tier lists it is a different question and is not answerable from persisted data.
/// </summary>
public sealed record BundleCatalogMember(
    int AppId,
    string Name,
    string? ImageUrl);

/// <summary>
/// The equal-share reading of one tier. <see cref="AllocatedPerGameMinor"/> is only set when the tier is
/// complete, priced, not an addon and has at least one eligible game: the tier price divided by that
/// count. It is an allocation estimate that requires buying the whole tier, never the price of any one
/// game, and it never becomes an offer.
/// </summary>
public sealed record BundleCatalogTierShare(
    int? PriceMinor,
    string? Currency,
    bool Addon,
    bool ItemsComplete,
    int ItemCount,
    int EligibleGameCount,
    int? AllocatedPerGameMinor,
    string AllocationStatus,
    bool RequiresWholeTierPurchase,
    /// <summary>
    /// The provider's "choose N of M" rule when it published one (gg.deals <c>gamesCount</c>), else
    /// null. It is the denominator the allocation uses when present; ITAD tiers leave it null and keep
    /// dividing by their eligible-game count. It is never the price of any one game.
    /// </summary>
    int? SelectionCount = null);

/// <summary>
/// One game of the current user's wishlist that this bundle provably contains. Exact identity only: the
/// item UUID matched inside a tier, or the bundle's own linked Steam appid. Never a title match, and
/// never another user's wishlist row.
/// </summary>
public sealed record BundleCatalogWishlistMatch(
    int AppId,
    string Name);

/// <summary>
/// One active persisted bundle as the catalog page reads it. Display data only: no price here is an
/// offer, none is written to <c>game_offers</c>, and none takes part in the price comparison or in the
/// existing aggregate tier savings. <see cref="WishlistMatches"/> is the current user's exact matches,
/// so the same bundle can be a match for one user and not for another.
/// </summary>
public sealed record BundleCatalogItem(
    string Source,
    string BundleKey,
    string Title,
    string? ShopId,
    string? ShopName,
    string? PageUrl,
    string? DealUrl,
    string? Details,
    DateTime? PublishedAt,
    DateTime? ExpiresAt,
    DateTime ObservedAt,
    IReadOnlyList<BundleCatalogMember> LinkedGames,
    IReadOnlyList<BundleCatalogTierShare> Tiers,
    IReadOnlyList<BundleCatalogWishlistMatch> WishlistMatches)
{
    /// <summary>True when any wishlist game was matched; a non-matching bundle stays fully visible.</summary>
    public bool HasWishlistMatch => WishlistMatches.Count > 0;
}

/// <summary>
/// Lightweight identity of one persisted bundle snapshot, read before its tiers. It is what the catalog
/// deduplicates on, so a bundle seen in two sources is collapsed without loading either tier payload.
/// </summary>
public sealed record BundleCatalogSnapshot(
    string Source,
    string BundleKey,
    string Title,
    DateTime? ExpiresAt,
    DateTime ObservedAt);

/// <summary>
/// One page of an already-deduplicated catalog. <see cref="Total"/> is the honest number of bundles in
/// the whole catalog, <see cref="Items"/> is only this page and <see cref="HasMore"/> says whether
/// another page exists. None of them is a fixed read bound.
/// </summary>
public sealed record BundleCatalogPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total,
    bool HasMore);

/// <summary>
/// Bounded read of the active bundle catalog. <see cref="Count"/> is how many bundles this page returned
/// and <see cref="Total"/> is how many exist in the deduplicated catalog, so a caller can tell a full
/// page from a truncated one without confusing either with a fixed cap.
/// </summary>
public sealed record BundleCatalog(
    IReadOnlyList<BundleCatalogItem> Items,
    int Count,
    int Total,
    int Page,
    int PageSize,
    bool HasMore);

/// <summary>
/// The catalog reads two ITAD sources and must collapse the same bundle observed in both. The pure rule
/// keeps the freshest snapshot and, on an exact timestamp tie, prefers <c>itad</c> (the per-game pass)
/// over <c>itad_global</c> (the sweep). Distinct bundle keys are all retained: a non-wishlist bundle is
/// never filtered out here.
/// </summary>
public static class BundleCatalogOrder
{
    public const string ItadSource = "itad";
    public const string ItadGlobalSource = "itad_global";

    /// <summary>
    /// gg.deals active-catalog source. Its bundle keys are a client-owned heuristic, so they are never
    /// deduplicated against the ITAD sources: the prefix and the source family below are two
    /// independent guards against collapsing two providers' bundles into one card.
    /// </summary>
    public const string GgDealsSource = "ggdeals";

    public static IReadOnlyList<BundleCatalogSnapshot> Deduplicate(IEnumerable<BundleCatalogSnapshot> snapshots) =>
        snapshots
            .GroupBy(snapshot => (Family(snapshot.Source), snapshot.BundleKey))
            .Select(group => group
                .OrderByDescending(snapshot => snapshot.ObservedAt)
                .ThenBy(snapshot => SourceRank(snapshot.Source))
                .ThenBy(snapshot => snapshot.Source, StringComparer.Ordinal)
                .First())
            // Soonest to expire first (null = no date, last), then title for a stable order across reads.
            .OrderBy(snapshot => snapshot.ExpiresAt is null)
            .ThenBy(snapshot => snapshot.ExpiresAt)
            .ThenBy(snapshot => snapshot.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.BundleKey, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Snapshot family used for deduplication. <c>itad</c> and <c>itad_global</c> are the same provider
    /// and may legitimately report the same bundle, so they share a family. Every other source is its
    /// own family, which is what prevents a cross-source key collision from hiding a bundle.
    /// </summary>
    private static string Family(string source) =>
        string.Equals(source, ItadSource, StringComparison.Ordinal) ||
        string.Equals(source, ItadGlobalSource, StringComparison.Ordinal)
            ? ItadSource
            : source;

    private static int SourceRank(string source) =>
        string.Equals(source, ItadSource, StringComparison.Ordinal) ? 0 : 1;
}

/// <summary>
/// Pagination bounds shared by the controller and the service. Page numbers are 1-based; an invalid page
/// is page 1 and an oversized page size is clamped, so the query stays bounded without lying about how
/// many bundles exist.
/// </summary>
public static class BundleCatalogPager
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public static int NormalizePage(int? page) => page is null or < 1 ? 1 : page.Value;

    public static int NormalizePageSize(int? pageSize) =>
        pageSize is null or < 1 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);

    public static BundleCatalogPage<T> Paginate<T>(IReadOnlyList<T> deduplicated, int? page, int? pageSize)
    {
        var normalizedPage = NormalizePage(page);
        var normalizedSize = NormalizePageSize(pageSize);
        var total = deduplicated.Count;
        var skip = (long)(normalizedPage - 1) * normalizedSize;
        var items = skip >= total
            ? (IReadOnlyList<T>)[]
            : deduplicated.Skip((int)skip).Take(normalizedSize).ToList();
        var hasMore = skip + items.Count < total;

        return new BundleCatalogPage<T>(items, normalizedPage, normalizedSize, total, hasMore);
    }
}
