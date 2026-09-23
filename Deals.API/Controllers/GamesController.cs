using System.Security.Claims;
using Deals.API.Models.Games;
using Deals.API.Security;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
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
    private readonly IGameTitleEditService _gameTitleEditService;

    public GamesController(
        IGameMergeService gameMergeService,
        ILibraryCoverService libraryCoverService,
        IGameTitleEditService gameTitleEditService)
    {
        _gameMergeService = gameMergeService;
        _libraryCoverService = libraryCoverService;
        _gameTitleEditService = gameTitleEditService;
    }

    /// <summary>
    /// Places the cover of a canonical game from an id the admin picked in a search: a Steam appid or an
    /// IGDB game id, exactly one of the two. The stored URL always comes from the provider the id names —
    /// never from the body and never from the client — and this is the one path that replaces an existing
    /// cover. A malformed body is a 400, a missing game or a provider row without artwork is a 404 and an
    /// unreachable provider is a 503; none of them writes.
    /// </summary>
    [HttpPut("{gameId:long}/cover")]
    public async Task<IActionResult> SetCover(
        long gameId,
        [FromBody] GameCoverRequest request,
        CancellationToken cancellationToken)
    {
        var command = request.ToCommand();
        var imageUrl = command.Source switch
        {
            GameCoverSource.Steam => await _libraryCoverService.SetCoverFromSteamAsync(
                gameId, command.SteamAppId!.Value, cancellationToken),
            GameCoverSource.Igdb => await _libraryCoverService.SetCoverFromIgdbAsync(
                gameId, command.IgdbId!.Value, cancellationToken),
            _ => throw new ArgumentException("La fuente de la portada no es válida.", nameof(request))
        };

        return Ok(new GameCoverResponse(imageUrl));
    }

    /// <summary>
    /// Edits the canonical title of a game (<c>games.title</c> + <c>games.normalized_title</c>), never the
    /// imported <c>user_library.title</c>: the library read-model projects the canonical value whenever the
    /// row has a game id, so a Playnite reimport cannot overwrite what the grid shows. The body is
    /// discriminated — <c>manual</c> with a typed title, or <c>igdb</c> with an id whose title the server
    /// reads from IGDB. A refused identity claim is 409 with the reason (never an exception: the global
    /// handler would drop the payload) and writes nothing; an unavailable or empty IGDB lookup is 503/404.
    /// </summary>
    [HttpPut("{gameId:long}/title")]
    public async Task<IActionResult> SetTitle(
        long gameId,
        [FromBody] GameTitleRequest request,
        CancellationToken cancellationToken)
    {
        var command = request.ToCommand();
        var outcome = await _gameTitleEditService.EditTitleAsync(gameId, command, cancellationToken);

        return outcome.Applied
            ? Ok(GameTitleEditResponse.From(outcome))
            : StatusCode(StatusCodes.Status409Conflict, GameTitleConflictResponse.From(outcome));
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
