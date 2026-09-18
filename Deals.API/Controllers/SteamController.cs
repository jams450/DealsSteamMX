using Deals.API.Models.Steam;
using Deals.API.Models.Reviews;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Deals.API.Controllers;

[ApiController]
[Route("api/steam")]
[Authorize(Policy = "UserWithId")]
[EnableRateLimiting("steam-read")]
public sealed class SteamController(
    ISteamGameService steamGameService,
    ICurrentUserService currentUserService,
    IGameOwnershipService gameOwnershipService,
    IReviewService reviewService) : ControllerBase
{
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<SteamSearchResponse>>> Search(
        [FromQuery] string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await steamGameService.SearchAsync(query, cancellationToken);
            return Ok(results.Select(ToResponse));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Message = "Steam is temporarily unavailable." });
        }
    }

    [HttpGet("suggestions")]
    public async Task<ActionResult<IReadOnlyList<SteamSearchResponse>>> Suggestions(
        [FromQuery] string? query,
        CancellationToken cancellationToken)
    {
        var results = await steamGameService.GetSuggestionsAsync(query, cancellationToken);
        return Ok(results.Select(ToResponse));
    }

    [HttpGet("games/{appId:int}")]
    public Task<ActionResult<SteamGameResponse>> GetGame(
        int appId,
        CancellationToken cancellationToken) =>
        FetchGame(appId, forceRefresh: false, cancellationToken);

    [HttpPost("games/{appId:int}/refresh")]
    [EnableRateLimiting("steam-refresh")]
    public Task<ActionResult<SteamGameResponse>> RefreshGame(
        int appId,
        CancellationToken cancellationToken) =>
        FetchGame(appId, forceRefresh: true, cancellationToken);

    private async Task<ActionResult<SteamGameResponse>> FetchGame(
        int appId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            var game = await steamGameService.GetByAppIdAsync(appId, forceRefresh, cancellationToken);
            if (game == null)
            {
                return NotFound();
            }

            var ownership = await ResolveOwnershipAsync(appId, cancellationToken);
            var reviews = await reviewService.GetForSteamAppAsync(
                currentUserService.GetRequiredUserId(),
                appId,
                cancellationToken);

            return Ok(ToResponse(game, ownership, reviews));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Message = "Steam is temporarily unavailable." });
        }
    }

    // Optional additive block: an unresolvable user id is "no ownership", never a failure.
    private async Task<GameOwnership> ResolveOwnershipAsync(int appId, CancellationToken cancellationToken)
    {
        var userId = currentUserService.GetUserId();
        return userId is > 0
            ? await gameOwnershipService.ResolveAsync(appId, userId.Value, cancellationToken)
            : GameOwnership.None;
    }

    private static SteamSearchResponse ToResponse(SteamSearchResult result) =>
        new(result.AppId, result.Name, result.Type, result.ImageUrl, result.HasDetails, result.RefreshedAt);

    private static SteamGameResponse ToResponse(
        SteamGameDetails game,
        GameOwnership ownership,
        IReadOnlyList<GameReview> reviews) =>
        new(game.AppId, game.Name, game.Type, game.ImageUrl, game.IsFree, game.Currency, game.InitialPriceMinor,
            game.CurrentPriceMinor, game.DiscountPercent, game.LowestPriceMinor, game.LowestPriceAt,
            game.Region, game.ObservedAt,
            (game.Offers ?? []).Select(ToResponse).ToList(),
            game.OffersRefreshedAt,
            game.OffersStale,
            game.GgDealsRefreshedAt,
            game.GgDealsStale,
            (game.Bundles ?? []).Select(ToResponse).ToList(),
            game.BundlesRefreshedAt,
            game.BundlesStale,
            new SteamGameOwnershipResponse(
                ownership.OwnedStores.ToArray(),
                ownership.HasGamePass,
                ownership.PossibleMatchStores.ToArray()),
            reviews.Select(ReviewResponse.From).ToList());

    private static SteamGameBundleResponse ToResponse(SteamGameBundle bundle) =>
        new(bundle.Source, bundle.BundleKey, bundle.Title, bundle.ShopId, bundle.ShopName,
            bundle.PageUrl, bundle.DealUrl, bundle.Details, bundle.PublishedAt, bundle.ExpiresAt,
            bundle.ObservedAt,
            bundle.Tiers.Select(ToResponse).ToList());

    private static SteamGameBundleTierResponse ToResponse(SteamGameBundleTier tier) =>
        new(tier.PriceMinor, tier.Currency, tier.Addon, tier.ItemsComplete,
            tier.Games.Select(ToResponse).ToList(),
            tier.Status, tier.Reason, tier.IndividualTotalMinor,
            // bundlePriceMinor is the tier's own published price, restated beside the savings so the
            // client never has to guess which side moved.
            tier.PriceMinor, tier.SavingsMinor, tier.SavingsPercent,
            tier.FxRate, tier.FxRateDate, tier.FxSource, tier.PricingType,
            tier.MxnIndividualTotalMinor, tier.MxnSavingsMinor);

    private static SteamGameBundleTierGameResponse ToResponse(SteamGameBundleTierGame game) =>
        new(game.Title, game.Type, game.PriceMinor, game.PriceCurrency);

    private static SteamGameOfferResponse ToResponse(SteamGameOffer offer) =>
        new(offer.Source, offer.OfferKey, offer.ShopId, offer.ShopName, offer.Classification,
            offer.OriginalCurrency, offer.OriginalRegularPriceMinor, offer.OriginalCurrentPriceMinor,
            offer.MxnRegularPriceMinor, offer.MxnCurrentPriceMinor, offer.FxRate, offer.FxRateDate,
            offer.FxSource, offer.PricingType, offer.DiscountPercent, offer.DealUrl, offer.ObservedAt,
            offer.DrmNames ?? [], offer.PlatformNames ?? [],
            offer.HistoryLowAllMinor, offer.HistoryLowCurrency);
}
