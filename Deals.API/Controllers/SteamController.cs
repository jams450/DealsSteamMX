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
    ISteamGameUserStateService userStateService) : ControllerBase
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

    [HttpGet("discover")]
    public async Task<ActionResult<IReadOnlyList<SteamDiscoverResponse>>> Discover(
        [FromQuery] string? list,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12,
        CancellationToken cancellationToken = default)
    {
        var items = await steamGameService.GetDiscoverAsync(list ?? string.Empty, page, pageSize, cancellationToken);
        return Ok(items.Select(ToResponse));
    }

    [HttpGet("games/{appId:int}")]
    public Task<ActionResult<SteamGameResponse>> GetGame(
        int appId,
        CancellationToken cancellationToken) =>
        FetchGame(appId, forceRefresh: false, interactive: false, cancellationToken);

    [HttpPost("games/{appId:int}/refresh")]
    [EnableRateLimiting("steam-refresh")]
    public Task<ActionResult<SteamGameResponse>> RefreshGame(
        int appId,
        CancellationToken cancellationToken) =>
        FetchGame(appId, forceRefresh: true, interactive: true, cancellationToken);

    private async Task<ActionResult<SteamGameResponse>> FetchGame(
        int appId,
        bool forceRefresh,
        bool interactive,
        CancellationToken cancellationToken)
    {
        try
        {
            var game = await steamGameService.GetByAppIdAsync(appId, forceRefresh, cancellationToken, interactive);
            if (game == null)
            {
                return NotFound();
            }

            var userState = await userStateService.GetAsync(
                currentUserService.GetRequiredUserId(),
                appId,
                cancellationToken);

            return Ok(ToResponse(game, userState.Ownership, userState.Reviews, userState.IsFavorite));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Message = "Steam is temporarily unavailable." });
        }
    }

    private static SteamSearchResponse ToResponse(SteamSearchResult result) =>
        new(result.AppId, result.Name, result.Type, result.ImageUrl, result.HasDetails, result.RefreshedAt);

    private static SteamDiscoverResponse ToResponse(SteamDiscoverItem result) =>
        new(result.AppId, result.Name, result.Type, result.ImageUrl, result.Currency,
            result.InitialPriceMinor, result.CurrentPriceMinor, result.LowestPriceMinor,
            result.ObservedAt, result.BestCurrentPriceMinor, result.BestPriceCurrency, result.BestPriceSource,
            result.BestPriceLabel, result.BestPriceClassification, result.BestPricePricingType,
            result.BestDiscountPercent, result.UsesSteamFallback);

    private static SteamGameResponse ToResponse(
        SteamGameDetails game,
        GameOwnership ownership,
        IReadOnlyList<GameReview> reviews,
        bool isFavorite) =>
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
            reviews.Select(ReviewResponse.From).ToList(),
            isFavorite,
            game.Publishers ?? []);

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
