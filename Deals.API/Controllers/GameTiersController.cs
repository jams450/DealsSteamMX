using Deals.BusinessLogic.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API.Controllers;

// La identidad del puesto es la partida reseñada (`reviewId`), no el juego: dos partidas del mismo juego
// en el mismo año son dos tarjetas y se guardan por separado. `gameId` viaja en la respuesta solo como
// ancla canónica.
public sealed record GameTierUpdateRequest(long ReviewId, string? Tier, int Position);
public sealed record GameTierResponse(
    long ReviewId,
    long GameId,
    string Title,
    string? ImageUrl,
    string Platform,
    bool IsGoty,
    string Tier,
    int Position);

[ApiController]
[Route("api/game-tiers")]
[Authorize(Policy = "UserWithId")]
public sealed class GameTiersController(IGameTierService service, ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>Años con reseñas fechadas del usuario, más reciente primero.</summary>
    [HttpGet("years")]
    public async Task<IActionResult> GetYears(CancellationToken cancellationToken)
    {
        var years = await service.GetYearsAsync(currentUser.GetRequiredUserId(), cancellationToken);
        return Ok(years);
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int year, CancellationToken cancellationToken)
    {
        var entries = await service.GetForYearAsync(currentUser.GetRequiredUserId(), year, cancellationToken);
        return Ok(entries.Select(entry => new GameTierResponse(
            entry.ReviewId,
            entry.GameId,
            entry.Title,
            entry.ImageUrl,
            entry.Platform,
            entry.IsGoty,
            entry.Tier,
            entry.Position)));
    }

    [HttpPut]
    public async Task<IActionResult> Set([FromQuery] int year, [FromBody] GameTierUpdateRequest request, CancellationToken cancellationToken)
    {
        var updated = await service.SetAsync(currentUser.GetRequiredUserId(), year, request.ReviewId, request.Tier, request.Position, cancellationToken);
        return updated ? NoContent() : NotFound();
    }
}
