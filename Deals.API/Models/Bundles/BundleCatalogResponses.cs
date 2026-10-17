namespace Deals.API.Models.Bundles;

/// <summary>One game confirmed to belong to a bundle through the persisted provider relation.</summary>
public sealed record BundleCatalogGameResponse(
    int AppId,
    string Name,
    string? ImageUrl);

/// <summary>
/// Equal-share reading of one tier. <c>AllocatedPerGameMinor</c> is only present when
/// <c>AllocationStatus</c> is <c>ok</c>, and it is always an estimate that requires buying the whole tier.
/// </summary>
public sealed record BundleCatalogTierResponse(
    int? PriceMinor,
    string? Currency,
    bool Addon,
    bool ItemsComplete,
    int ItemCount,
    int EligibleGameCount,
    int? AllocatedPerGameMinor,
    string AllocationStatus,
    bool RequiresWholeTierPurchase);

/// <summary>One active persisted ITAD bundle. Display only: never an offer and never a savings figure.</summary>
public sealed record BundleCatalogItemResponse(
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
    IReadOnlyList<BundleCatalogGameResponse> LinkedGames,
    IReadOnlyList<BundleCatalogTierResponse> Tiers);

/// <summary>Bounded catalog read. <c>MaxItems</c> is the hard bound the read applied.</summary>
public sealed record BundleCatalogResponse(
    IReadOnlyList<BundleCatalogItemResponse> Items,
    int Count,
    int MaxItems);
