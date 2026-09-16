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
    public async Task<ActionResult<SteamGameResponse>> GetGame(int appId, CancellationToken cancellationToken)
    {
        try
        {
            var game = await steamGameService.GetByAppIdAsync(appId, cancellationToken);
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
            game.Region, game.ObservedAt);
}
