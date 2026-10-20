namespace Deals.API.Models.Bundles;

/// <summary>One game confirmed to belong to a bundle through the persisted provider relation.</summary>
public sealed record BundleCatalogGameResponse(
    int AppId,
    string Name,
    string? ImageUrl);

/// <summary>
/// One current-user wishlist game this bundle provably contains. Identity only: an exact tier item UUID
/// or the bundle's own linked appid, never a title match. The list is scoped to the authenticated user,
/// so no other user's wishlist is ever exposed.
/// </summary>
public sealed record BundleWishlistMatchResponse(
    int AppId,
    string Name);

/// <summary>
/// One amount read in USD and MXN. The side that matches the tier's own currency is the published
/// figure and the other one is an estimate; a side with no persisted rate is <c>null</c>, never 0, so the
/// UI can say "no disponible" instead of painting a fabricated number. <c>FxRate</c>,
/// <c>FxRateDate</c> and <c>FxSource</c> are the exact persisted rate row that produced the converted
/// side, and all null when nothing was converted. <c>PricingType</c> is <c>fx_estimate</c> when a rate
/// produced the other side, <c>regional</c> when the amount is already MXN and no rate was available, and
/// <c>unconverted</c> when no honest MXN reading exists (unknown price, unsupported currency, unusable
/// rate).
/// </summary>
public sealed record BundleMoneyResponse(
    int? UsdMinor,
    int? MxnMinor,
    decimal? FxRate,
    DateOnly? FxRateDate,
    string? FxSource,
    string PricingType);

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
    bool RequiresWholeTierPurchase,
    /// <summary>
    /// Provider "choose N of M" rule when it published one (gg.deals), else null. It is the denominator
    /// the equal share uses when present and is never the price of any one game.
    /// </summary>
    int? SelectionCount,
    /// <summary>
    /// The tier total in USD and MXN. Additive on purpose: <c>PriceMinor</c>/<c>Currency</c> remain the
    /// published, native figure and are never rewritten.
    /// </summary>
    BundleMoneyResponse? PriceMoney = null,
    /// <summary>
    /// The equal share in USD and MXN; null exactly when there is no allocation to read.
    /// </summary>
    BundleMoneyResponse? AllocatedPerGameMoney = null);

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
    IReadOnlyList<BundleCatalogTierResponse> Tiers,
    bool HasWishlistMatch,
    IReadOnlyList<BundleWishlistMatchResponse> WishlistMatches);

/// <summary>
/// One honest page of the catalog. <c>Total</c> is how many bundles exist after deduplication,
/// <c>Count</c> is how many this page returned, and <c>HasMore</c> says whether a next page exists.
/// A non-wishlist bundle is always included: this is the whole catalog, not only the wishlist matches.
/// </summary>
public sealed record BundleCatalogResponse(
    IReadOnlyList<BundleCatalogItemResponse> Items,
    int Count,
    int Total,
    int Page,
    int PageSize,
    bool HasMore);
