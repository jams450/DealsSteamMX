using System.Security.Claims;
using Deals.API.Models.Games;
using Deals.API.Security;
using Deals.BusinessLogic.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API.Controllers;

/// <summary>
/// Manual maintenance of canonical game identity. Admin-only: a merge rewrites shared identity and
/// destroys the losing row, so it is never a user-facing action.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "AdminWithId")]
public class GamesController : ControllerBase
{
    private readonly IGameMergeService _gameMergeService;
    private readonly ILibraryCoverService _libraryCoverService;

    public GamesController(IGameMergeService gameMergeService, ILibraryCoverService libraryCoverService)
    {
        _gameMergeService = gameMergeService;
        _libraryCoverService = libraryCoverService;
    }

    /// <summary>
    /// Places the cover of a canonical game from a Steam appid the admin picked in a search. The stored URL
    /// always comes from Steam, and this is the one path that replaces an existing cover.
    /// </summary>
    [HttpPut("{gameId:long}/cover")]
    public async Task<IActionResult> SetCover(
        long gameId,
        [FromBody] GameCoverRequest request,
        CancellationToken cancellationToken)
    {
        var imageUrl = await _libraryCoverService.SetCoverFromSteamAsync(gameId, request.SteamAppId, cancellationToken);
        return Ok(new GameCoverResponse(imageUrl));
    }

    /// <summary>Duplicate candidates for the calling admin's own library. Suggestions only, read-only.</summary>
    [HttpGet("merge-suggestions")]
    public async Task<IActionResult> GetMergeSuggestions(CancellationToken cancellationToken)
    {
        var groups = await _gameMergeService.FindDuplicateGroupsAsync(GetUserId(), cancellationToken);
        return Ok(groups.Select(DuplicateGroupResponse.From));
    }

    /// <summary>
    /// Merges the game in the route into <c>intoGameId</c>. A refused merge is 409 with the reason (never
    /// an exception: the global handler would drop the payload).
    /// </summary>
    [HttpPost("{absorbedGameId:long}/merge")]
    public async Task<IActionResult> Merge(
        long absorbedGameId,
        [FromBody] GameMergeRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await _gameMergeService.MergeAsync(
            absorbedGameId,
            request.IntoGameId,
            GetUserId(),
            GetActorName(),
            cancellationToken);

        var response = GameMergeOutcomeResponse.From(outcome);
        return outcome.Applied
            ? Ok(response)
            : StatusCode(StatusCodes.Status409Conflict, response);
    }

    // Same pattern as LibraryController: the id claim may be NameIdentifier or the raw "sub".
    private int GetUserId()
    {
        var value = User.FindFirstValue(ClaimNames.NameIdentifier) ?? User.FindFirstValue(ClaimNames.Subject);
        return int.TryParse(value, out var userId) && userId > 0
            ? userId
            : throw new UnauthorizedAccessException("Missing or invalid user identity claim");
    }

    private string GetActorName() =>
        User.FindFirstValue(ClaimNames.Name) ??
        User.FindFirstValue("name") ??
        "System";
}
