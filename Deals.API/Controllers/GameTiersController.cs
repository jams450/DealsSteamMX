using Deals.BusinessLogic.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API.Controllers;

public sealed record GameTierUpdateRequest(long GameId, string? Tier, int Position);
public sealed record GameTierResponse(long GameId, string Title, string? ImageUrl, string Tier, int Position);

[ApiController]
[Route("api/game-tiers")]
[Authorize(Policy = "UserWithId")]
public sealed class GameTiersController(IGameTierService service, ICurrentUserService currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int year, CancellationToken cancellationToken)
    {
        var entries = await service.GetForYearAsync(currentUser.GetRequiredUserId(), year, cancellationToken);
        return Ok(entries.Select(entry => new GameTierResponse(entry.GameId, entry.Title, entry.ImageUrl, entry.Tier, entry.Position)));
    }

    [HttpPut]
    public async Task<IActionResult> Set([FromQuery] int year, [FromBody] GameTierUpdateRequest request, CancellationToken cancellationToken)
    {
        var updated = await service.SetAsync(currentUser.GetRequiredUserId(), year, request.GameId, request.Tier, request.Position, cancellationToken);
        return updated ? NoContent() : NotFound();
    }
}
