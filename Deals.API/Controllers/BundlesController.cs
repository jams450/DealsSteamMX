using Deals.API.Models.Bundles;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Deals.API.Controllers;

/// <summary>
/// Authenticated bundle catalog and acquisition. It answers from the bundles already persisted by the game
/// refresh and the global sweep and never calls a provider, so it cannot fan out to an external source.
/// The authenticated user comes from <see cref="ICurrentUserService"/> only - the query string can page
/// the catalog or name a bundle but can never choose whose wishlist, purchases or duplicates are read. The
/// existing per-game bundle summary is served by the game detail payload and is unaffected.
///
/// <para>
/// The purchase endpoints are the S3-S6 contract: a read-only preview, one atomic confirm that is
/// idempotent per (user, source, bundle), the caller's own purchase and duplicate inventories, and the
/// read-only canonical candidate search used to link an item the provider identified poorly.
/// </para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "UserWithId")]
[EnableRateLimiting("steam-read")]
public sealed class BundlesController(
    BundleCatalogService bundleCatalogService,
    IBundlePurchaseService bundlePurchaseService,
    ICurrentUserService currentUserService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<BundleCatalogResponse>> Get(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var catalog = await bundleCatalogService.ListActiveAsync(
            currentUserService.GetRequiredUserId(),
            page,
            pageSize,
            cancellationToken);

        return Ok(new BundleCatalogResponse(
            catalog.Items.Select(ToResponse).ToList(),
            catalog.Count,
            catalog.Total,
            catalog.Page,
            catalog.PageSize,
            catalog.HasMore));
    }

    /// <summary>Read-only purchase preview of one persisted bundle for the authenticated caller.</summary>
    [HttpGet("purchases/preview")]
    public async Task<ActionResult<BundlePurchasePreviewResponse>> GetPurchasePreview(
        [FromQuery] string? source,
        [FromQuery] string? bundleKey,
        [FromQuery] string? platform,
        CancellationToken cancellationToken)
    {
        var preview = await bundlePurchaseService.PreviewAsync(
            currentUserService.GetRequiredUserId(),
            source,
            bundleKey,
            platform,
            cancellationToken);

        return preview is null ? NotFound() : Ok(ToResponse(preview));
    }

    /// <summary>
    /// Confirms one bundle purchase. The response is 404 only when the bundle snapshot does not exist; a
    /// repeat of the same purchase answers 200 with <c>created = false</c> and writes nothing.
    /// </summary>
    [HttpPost("purchases")]
    public async Task<ActionResult<BundlePurchaseConfirmResponse>> ConfirmPurchase(
        [FromBody] BundlePurchaseConfirmRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentException("El cuerpo de la solicitud es obligatorio", nameof(request));
        }

        var result = await bundlePurchaseService.PurchaseAsync(
            currentUserService.GetRequiredUserId(),
            request.ToBusinessRequest(),
            cancellationToken);

        return result is null ? NotFound() : Ok(ToResponse(result));
    }

    /// <summary>The authenticated caller's purchases, newest first.</summary>
    [HttpGet("purchases")]
    public async Task<ActionResult<BundlePurchaseListEnvelope>> GetPurchases(CancellationToken cancellationToken)
    {
        var purchases = await bundlePurchaseService.ListAsync(
            currentUserService.GetRequiredUserId(),
            cancellationToken);

        return Ok(new BundlePurchaseListEnvelope(
            purchases.Select(ToResponse).ToList(),
            purchases.Count));
    }

    /// <summary>The authenticated caller's duplicate inventory: games skipped because they were owned.</summary>
    [HttpGet("purchases/duplicates")]
    public async Task<ActionResult<BundlePurchaseDuplicateEnvelope>> GetDuplicateInventory(CancellationToken cancellationToken)
    {
        var duplicates = await bundlePurchaseService.ListDuplicatesAsync(
            currentUserService.GetRequiredUserId(),
            cancellationToken);

        return Ok(new BundlePurchaseDuplicateEnvelope(
            duplicates.Select(ToResponse).ToList(),
            duplicates.Count));
    }

    /// <summary>
    /// Read-only canonical candidates by title, for the manual link of an item the provider did not
    /// identify. Nothing is created and nothing is selected by the server.
    /// </summary>
    [HttpGet("catalog-candidates")]
    public async Task<ActionResult<BundlePurchaseCandidateEnvelope>> FindCatalogCandidates(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var candidates = await bundlePurchaseService.FindCandidatesAsync(
            currentUserService.GetRequiredUserId(),
            q,
            limit,
            cancellationToken);

        return Ok(new BundlePurchaseCandidateEnvelope(
            candidates
                .Select(candidate => new BundlePurchaseCandidateResponse(
                    candidate.GameId,
                    candidate.Title,
                    candidate.ReleaseYear,
                    candidate.ImageUrl,
                    candidate.InLibrary))
                .ToList(),
            candidates.Count));
    }

    private static BundlePurchasePreviewResponse ToResponse(BundlePurchasePreview preview) =>
        new(
            preview.Snapshot.Source,
            preview.Snapshot.BundleKey,
            preview.Snapshot.Title,
            preview.Snapshot.ShopId,
            preview.Snapshot.ShopName,
            preview.Snapshot.PageUrl,
            preview.Snapshot.DealUrl,
            preview.Snapshot.Details,
            preview.Snapshot.PublishedAt,
            preview.Snapshot.ExpiresAt,
            preview.Snapshot.ObservedAt,
            preview.SnapshotHash,
            preview.Purchased,
            preview.PurchaseId,
            preview.Tiers.Select(tier => new BundlePurchasePreviewTierResponse(
                tier.TierKey,
                tier.TierIndex,
                tier.PriceMinor,
                tier.Currency,
                tier.Addon,
                tier.ItemsComplete,
                tier.SelectionCount,
                tier.SelectionMode,
                tier.RequiredSelectionCount,
                tier.Importable,
                tier.BlockedReason,
                tier.Items.Select(item => new BundlePurchasePreviewItemResponse(
                    item.ItemKey,
                    item.Title,
                    item.Type,
                    item.PriceMinor,
                    item.Currency,
                    item.ItadGameId,
                    item.Url,
                    item.Eligible,
                    item.CanonicalGameId,
                    item.CanonicalSource,
                    item.RequiresManualLink,
                    item.Owned,
                    item.OwnedUserLibraryId,
                    item.OwnedPlatform))
                    .ToList()))
                .ToList());

    private static BundlePurchaseConfirmResponse ToResponse(BundlePurchaseResult result) =>
        new(
            result.PurchaseId,
            result.Created,
            result.AlreadyReported,
            result.Source,
            result.BundleKey,
            result.Title,
            result.TierKey,
            result.Platform,
            result.SnapshotHash,
            result.SelectedCount,
            result.ImportedCount,
            result.DuplicateCount,
            result.WishlistRemovedCount,
            result.Games.Select(ToResponse).ToList(),
            result.Duplicates.Select(ToResponse).ToList());

    private static BundlePurchaseListResponse ToResponse(BundlePurchaseRecord record) =>
        new(
            record.PurchaseId,
            record.Source,
            record.BundleKey,
            record.Title,
            record.ShopId,
            record.ShopName,
            record.PageUrl,
            record.DealUrl,
            record.PublishedAt,
            record.ExpiresAt,
            record.ObservedAt,
            record.TierKey,
            record.TierIndex,
            record.TierPriceMinor,
            record.TierCurrency,
            record.TierAddon,
            record.TierItemsComplete,
            record.TierSelectionCount,
            record.SelectionMode,
            record.SelectedCount,
            record.Platform,
            record.AcquiredAt,
            record.AcquiredStore,
            record.AcquiredStoreType,
            record.AcquisitionProvider,
            record.SnapshotHash,
            record.CreatedAt,
            record.ImportedCount,
            record.DuplicateCount,
            record.Games.Select(ToResponse).ToList());

    private static BundlePurchaseGameResponse ToResponse(BundlePurchaseGameResult game) =>
        new(
            game.ItemKey,
            game.Title,
            game.GameId,
            game.Platform,
            game.Outcome,
            game.UserLibraryId,
            game.CanonicalSource,
            game.WishlistRemoved);

    private static BundlePurchaseDuplicateResponse ToResponse(BundlePurchaseDuplicateEntry entry) =>
        new(
            entry.PurchaseId,
            entry.Source,
            entry.BundleKey,
            entry.BundleTitle,
            entry.AcquiredAt,
            entry.ItemKey,
            entry.Title,
            entry.GameId,
            entry.Platform,
            entry.UserLibraryId,
            entry.ExistingTitle);

    private static BundleCatalogItemResponse ToResponse(BundleCatalogItem item) =>
        new(
            item.Source,
            item.BundleKey,
            item.Title,
            item.ShopId,
            item.ShopName,
            item.PageUrl,
            item.DealUrl,
            item.Details,
            item.PublishedAt,
            item.ExpiresAt,
            item.ObservedAt,
            item.LinkedGames
                .Select(game => new BundleCatalogGameResponse(game.AppId, game.Name, game.ImageUrl))
                .ToList(),
            item.Tiers.Select(ToResponse).ToList(),
            item.HasWishlistMatch,
            item.WishlistMatches
                .Select(match => new BundleWishlistMatchResponse(match.AppId, match.Name))
                .ToList());

    private static BundleCatalogTierResponse ToResponse(BundleCatalogTierShare share) =>
        new(
            share.PriceMinor,
            share.Currency,
            share.Addon,
            share.ItemsComplete,
            share.ItemCount,
            share.EligibleGameCount,
            share.AllocatedPerGameMinor,
            share.AllocationStatus,
            share.RequiresWholeTierPurchase,
            share.SelectionCount,
            ToResponse(share.PriceMoney),
            ToResponse(share.AllocatedPerGameMoney));

    private static BundleMoneyResponse? ToResponse(BundleMoney? money) =>
        money is null
            ? null
            : new BundleMoneyResponse(
                money.UsdMinor,
                money.MxnMinor,
                money.FxRate,
                money.FxRateDate,
                money.FxSource,
                money.PricingType);
}
