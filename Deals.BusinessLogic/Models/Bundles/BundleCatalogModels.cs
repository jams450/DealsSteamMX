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
    bool RequiresWholeTierPurchase);

/// <summary>
/// One active persisted bundle as the catalog page reads it. Display data only: no price here is an
/// offer, none is written to <c>game_offers</c>, and none takes part in the price comparison or in the
/// existing aggregate tier savings.
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
    IReadOnlyList<BundleCatalogTierShare> Tiers);

/// <summary>
/// Bounded read of the active bundle catalog. <see cref="Count"/> is how many bundles were returned and
/// <see cref="MaxItems"/> is the hard bound the read applied, so a caller can tell a full page from a
/// truncated one.
/// </summary>
public sealed record BundleCatalog(
    IReadOnlyList<BundleCatalogItem> Items,
    int Count,
    int MaxItems);
