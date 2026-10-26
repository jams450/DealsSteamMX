using Deals.BusinessLogic.Interfaces;
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
/// It also answers `GET /api/catalogdeals/freshness`: the price-coverage monitor of the *requesting user's
/// wishlist* (`recent` / `stale` / `missing` over 72 hours, plus per-provider price and consultation dates).
/// The three category tabs stay global; only this monitor and the manual pending-price pass are personal (spec
/// S4, user correction «es solo whislist»). Both surfaces answer from the same persisted rows and never call a
/// provider, write a price, touch identity or read a bundle. Steam-less wished games are first-class members:
/// an offer anchored to `game_id` alone is coverage, and it is monitored even though it has no Steam app id.
///
/// Security: `UserWithId` plus the shared `steam-read` budget (60/min per caller). The user comes from the
/// token and is never read from the query string, which can page the list but cannot choose whose list it is.
/// The wishlist scope is resolved from that id alone: the library, the favourites, other accounts and the
/// configured notification operator are not consulted, and there is no fallback account.
///
/// Freshness and the scoring threshold are the notifier's own settings (<see cref="NtfyOptions"/>), so a card
/// and a notification can never disagree about what "fresh" or "viable" means; disabling the notifier
/// (<see cref="NtfyOptions.Enabled"/>) only stops the sending job, it does not empty this page. The monitor's
/// 72-hour window is deliberately its own constant (<see cref="CatalogFreshness.Hours"/>): notification
/// freshness is unchanged by this endpoint and the monitor's window cannot be moved by a notifier setting.
///
/// An unknown `category` or `status` is rejected with 400 by the shared exception handler (Spanish
/// ProblemDetails); it is never silently defaulted to another value.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "UserWithId")]
[EnableRateLimiting("steam-read")]
public sealed class CatalogDealsController(
    CatalogDealService catalogDealService,
    CatalogFreshnessService catalogFreshnessService,
    ICurrentUserService currentUser,
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
    /// Price-coverage monitor of the requesting user's wishlist (spec S4): `?status=all|recent|stale|missing`,
    /// one page, and the four counters in the same envelope. `total` is the selected filter's total; `counts` is
    /// the whole wishlist and is never narrowed by the filter. Every instant is ISO UTC and `null` means
    /// unknown — never "never attempted" and never "failed".
    ///
    /// `unresolvedWishlistEntries` is additive and separate from `counts`: it counts wishlist entries (rows) that
    /// no exact identity path could attribute to an existing game. They get no synthetic game row, and they are
    /// not folded into `missing` either — `missing` is about games the monitor knows.
    /// </summary>
    [HttpGet("freshness")]
    public async Task<ActionResult<CatalogFreshnessResponse>> GetFreshnessAsync(
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        // The scope is the caller's own list. `UserWithId` already guarantees a valid id claim; failing closed
        // here (instead of defaulting to some account) keeps a malformed token from reading anybody's prices.
        var userId = currentUser.GetUserId()
            ?? throw new UnauthorizedAccessException("La sesión no identifica a un usuario válido.");

        var freshness = await catalogFreshnessService.ListAsync(
            userId,
            status,
            page ?? 1,
            pageSize ?? CatalogDealPager.DefaultPageSize,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);

        return Ok(new CatalogFreshnessResponse(
            freshness.AsOfUtc,
            freshness.FreshnessHours,
            freshness.Page,
            freshness.PageSize,
            freshness.Total,
            new CatalogFreshnessCountsResponse(
                freshness.Counts.Total,
                freshness.Counts.Recent,
                freshness.Counts.Stale,
                freshness.Counts.Missing),
            freshness.UnresolvedWishlistEntries,
            freshness.Items.Select(item => new CatalogFreshnessItemResponse(
                item.GameId,
                item.Name,
                item.ImageUrl,
                item.SteamAppId,
                item.Status,
                item.PriceObservedAtUtc,
                item.ChosenPriceMinor,
                item.ChosenShopName,
                item.ChosenSource,
                item.ChosenPricingType,
                item.Providers
                    .Select(provider => new CatalogFreshnessProviderResponse(
                        provider.Provider,
                        provider.PriceObservedAtUtc,
                        provider.LastCheckedAtUtc))
                    .ToList()))
                .ToList()));
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

    /// <summary>
    /// One monitor row: a game on the caller's wishlist, its coverage bucket, the price that bucket came from and the
    /// per-provider dates that reveal partial staleness. `name`/`imageUrl`/`steamAppId` are the same identity the
    /// catalog cards use; `steamAppId` is null for a game Steam does not sell.
    ///
    /// `priceObservedAtUtc` is the date of the CHEAPEST stored usable comparable price (user correction S9), and
    /// `chosenPriceMinor`, `chosenShopName`, `chosenSource` and `chosenPricingType` describe that same price: the
    /// four additive fields are frozen for the discover cards, so a card never mixes one row's price with another
    /// row's store. All five are `null` together when the game has no usable comparable price.
    /// </summary>
    public sealed record CatalogFreshnessItemResponse(
        long GameId,
        string Name,
        string? ImageUrl,
        int? SteamAppId,
        string Status,
        DateTime? PriceObservedAtUtc,
        long? ChosenPriceMinor,
        string? ChosenShopName,
        string? ChosenSource,
        string? ChosenPricingType,
        IReadOnlyList<CatalogFreshnessProviderResponse> Providers);

    /// <summary>
    /// One provider of one game: when it last reported a usable price and when it was last consulted. Both are
    /// independent facts and both are nullable (`null` = unknown). A provider listed with no price date and a
    /// consultation date was asked and had nothing comparable — that is not a failure and not a stale price.
    /// </summary>
    public sealed record CatalogFreshnessProviderResponse(
        string Provider,
        DateTime? PriceObservedAtUtc,
        DateTime? LastCheckedAtUtc);

    /// <summary>The four wishlist counters; `total` is every canonical game on the caller's list, whatever the filter is.</summary>
    public sealed record CatalogFreshnessCountsResponse(int Total, int Recent, int Stale, int Missing);

    /// <summary>
    /// One page of the monitor: the reference instant, the window it was evaluated with, the returned page, the
    /// selected filter's total, the whole-wishlist counters and — additive, outside the counters — how many
    /// wishlist entries could not be attributed to a game.
    /// </summary>
    public sealed record CatalogFreshnessResponse(
        DateTime AsOfUtc,
        int FreshnessHours,
        int Page,
        int PageSize,
        int Total,
        CatalogFreshnessCountsResponse Counts,
        int UnresolvedWishlistEntries,
        IReadOnlyList<CatalogFreshnessItemResponse> Items);

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
