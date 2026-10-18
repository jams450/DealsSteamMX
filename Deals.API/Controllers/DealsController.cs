using Deals.API.Models.Discovery;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Deals.API.Controllers;

/// <summary>
/// Authenticated, read-only global deals feed. It answers from the deals already persisted by the
/// background sweep and never calls a provider, so it cannot fan out to ITAD on a page load and it is
/// safe to refresh manually. The authenticated user comes from <see cref="ICurrentUserService"/> only:
/// the query string can page the feed but can never choose whose wishlist is excluded.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "UserWithId")]
[EnableRateLimiting("steam-read")]
public sealed class DealsController(
    GlobalDealCatalogService globalDealCatalogService,
    ICurrentUserService currentUserService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GlobalDealCatalogResponse>> Get(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var catalog = await globalDealCatalogService.ListAsync(
            currentUserService.GetRequiredUserId(),
            page,
            pageSize,
            cancellationToken);

        return Ok(new GlobalDealCatalogResponse(
            catalog.Items.Select(ToResponse).ToList(),
            catalog.Count,
            catalog.Total,
            catalog.Page,
            catalog.PageSize,
            catalog.HasMore));
    }

    private static GlobalDealItemResponse ToResponse(GlobalDealCatalogItem item) =>
        new(
            item.DealKey,
            item.ItadGameId,
            item.GameTitle,
            item.GameSlug,
            item.GameType,
            item.GameMature,
            item.ShopId,
            item.ShopName,
            item.Currency,
            item.CurrentPriceMinor,
            item.RegularPriceMinor,
            item.DiscountPercent,
            item.MxnCurrentPriceMinor,
            item.PricingType,
            item.FxRate,
            item.FxRateDate,
            item.FxSource,
            item.HistoryLowMinor,
            item.HistoryLowCurrency,
            item.IsAtHistoryLow,
            item.GoodDealReason,
            item.DealUrl,
            item.ExpiresAt,
            item.ObservedAt);
}
