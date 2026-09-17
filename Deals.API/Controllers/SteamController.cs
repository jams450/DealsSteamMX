using Deals.API.Models.Steam;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Steam;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API.Controllers;

[ApiController]
[Route("api/steam")]
[Authorize(Policy = "UserWithId")]
public sealed class SteamController(ISteamGameService steamGameService) : ControllerBase
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
            return game == null ? NotFound() : Ok(ToResponse(game));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Message = "Steam is temporarily unavailable." });
        }
    }

    private static SteamSearchResponse ToResponse(SteamSearchResult result) =>
        new(result.AppId, result.Name, result.Type, result.ImageUrl);

    private static SteamGameResponse ToResponse(SteamGameDetails game) =>
        new(game.AppId, game.Name, game.Type, game.ImageUrl, game.IsFree, game.Currency, game.InitialPriceMinor,
            game.CurrentPriceMinor, game.DiscountPercent, game.LowestPriceMinor, game.LowestPriceAt,
            game.Region, game.ObservedAt,
            (game.Offers ?? []).Select(ToResponse).ToList(),
            game.OffersRefreshedAt,
            game.OffersStale);

    private static SteamGameOfferResponse ToResponse(SteamGameOffer offer) =>
        new(offer.Source, offer.OfferKey, offer.ShopId, offer.ShopName, offer.Classification,
            offer.OriginalCurrency, offer.OriginalRegularPriceMinor, offer.OriginalCurrentPriceMinor,
            offer.MxnRegularPriceMinor, offer.MxnCurrentPriceMinor, offer.FxRate, offer.FxRateDate,
            offer.FxSource, offer.PricingType, offer.DiscountPercent, offer.DealUrl, offer.ObservedAt,
            offer.DrmNames ?? [], offer.PlatformNames ?? []);
}
