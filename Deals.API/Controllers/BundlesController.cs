using Deals.API.Models.Bundles;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Deals.API.Controllers;

/// <summary>
/// Authenticated, read-only bundle catalog. It answers from the bundles already persisted by the game
/// refresh and the global sweep and never calls a provider, so it cannot fan out to an external source.
/// The authenticated user comes from <see cref="ICurrentUserService"/> only - the query string can page
/// the catalog but can never choose whose wishlist is highlighted. The existing per-game bundle summary is
/// served by the game detail payload and is unaffected.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "UserWithId")]
[EnableRateLimiting("steam-read")]
public sealed class BundlesController(
    BundleCatalogService bundleCatalogService,
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
