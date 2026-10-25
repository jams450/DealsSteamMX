using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Deals.API.Controllers;

/// <summary>
/// Authenticated, read-only discover catalog (spec S1/S2/S3/S6): one page of one category plus the three tab
/// counters in the same envelope.
///
/// The three categories are the three independent rules of <see cref="DealAlertPolicy"/> — `under50`
/// (strictly below 50.00 MXN), `historic` (verified provider history low in the offer's own currency) and
/// `score` (the existing wishlist deal score strictly above 7). They are not a funnel: `historic` and `score`
/// are not limited by the cheap threshold, and no new formula, discount or recency category exists here.
///
/// It answers from persisted snapshots only — the same persisted rows the notifier reads — so a page load
/// never calls a provider, never writes a price, never touches identity and never sees a bundle. Steam-less
/// games are first-class members: an offer anchored to `game_id` alone gets a card with its own provider URL.
///
/// Security: `UserWithId` plus the shared `steam-read` budget (60/min per caller). The user comes from the
/// token and is never read here; the query string can page the catalog but cannot choose whose catalog it is.
/// Nothing about the caller's wishlist, favorites or the notification operator is read: `/discover` is the
/// global catalog (spec S2), and the notifier's personal scope is a separate surface.
///
/// Freshness and the scoring threshold are the notifier's own settings (<see cref="NtfyOptions"/>), so a card
/// and a notification can never disagree about what "fresh" or "viable" means; disabling the notifier
/// (<see cref="NtfyOptions.Enabled"/>) only stops the sending job, it does not empty this page.
///
/// An unknown `category` is rejected with 400 by the shared exception handler (Spanish ProblemDetails); it is
/// never silently defaulted to another category.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "UserWithId")]
[EnableRateLimiting("steam-read")]
public sealed class CatalogDealsController(
    CatalogDealService catalogDealService,
    IOptions<NtfyOptions> ntfyOptions,
    TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CatalogDealPageResponse>> Get(
        [FromQuery] string? category,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var options = ntfyOptions.Value;

        // Same context the producer builds: the policy is evaluated with one instant and one freshness
        // window, and paging inputs are clamped by the service, which reports the page it actually returned.
        var context = new DealAlertContext(
            clock.GetUtcNow().UtcDateTime,
            TimeSpan.FromHours(options.MaxSnapshotAgeHours),
            options.MinViableDiscountPercent);

        var catalog = await catalogDealService.ListAsync(
            category ?? CatalogDealCategories.Under50,
            page ?? 1,
            pageSize ?? CatalogDealPager.DefaultPageSize,
            context,
            cancellationToken);

        return Ok(new CatalogDealPageResponse(
            catalog.Category,
            catalog.Items.Select(ToResponse).ToList(),
            catalog.Count,
            catalog.Total,
            catalog.Under50Total,
            catalog.HistoricTotal,
            catalog.ScoreTotal,
            catalog.Page,
            catalog.PageSize,
            catalog.HasMore));
    }

    /// <summary>
    /// The API contract of one card. Price, shop label, classification, pricing type, history low and URL are
    /// copied from the one qualifying offer, so the response cannot mix two rows.
    /// <see cref="SteamAppId"/> is nullable on purpose: a game Steam does not sell has no internal detail
    /// route, and the client must use the offer's own URL instead.
    /// </summary>
    public sealed record CatalogDealItemResponse(
        long GameId,
        int? SteamAppId,
        string Title,
        string? ImageUrl,
        string Source,
        string ShopName,
        string Classification,
        string PricingType,
        int MxnPriceMinor,
        int? OriginalPriceMinor,
        string OriginalCurrency,
        bool UsesFxEstimate,
        int? HistoryLowMinor,
        string? HistoryLowCurrency,
        string? DealUrl,
        double? Score,
        IReadOnlyList<string> Reasons,
        DateTime ObservedAt);

    /// <summary>
    /// One page plus the three tab totals (<c>count</c> = cards on this page, <c>total</c> = games in the
    /// selected category), so the UI paints every tab counter with a single request.
    /// </summary>
    public sealed record CatalogDealPageResponse(
        string Category,
        IReadOnlyList<CatalogDealItemResponse> Items,
        int Count,
        int Total,
        int Under50Total,
        int HistoricTotal,
        int ScoreTotal,
        int Page,
        int PageSize,
        bool HasMore);

    private static CatalogDealItemResponse ToResponse(CatalogDealItem item) =>
        new(
            item.GameId,
            item.SteamAppId,
            item.Title,
            item.ImageUrl,
            item.Source,
            item.ShopName,
            item.Classification,
            item.PricingType,
            item.MxnPriceMinor,
            item.OriginalPriceMinor,
            item.OriginalCurrency,
            item.UsesFxEstimate,
            item.HistoryLowMinor,
            item.HistoryLowCurrency,
            item.DealUrl,
            item.Score,
            item.Reasons,
            item.ObservedAt);
}
