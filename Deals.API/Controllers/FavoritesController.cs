using Deals.BusinessLogic.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API.Controllers;

/// <summary>
/// Wire contract of the favorite toggle. Exactly one identifier travels: <c>gameId</c> from the library (a
/// row's canonical id) or <c>steamAppId</c> from the game detail page (which has no canonical id on hand).
/// </summary>
public sealed record FavoriteRequest(long? GameId, int? SteamAppId);

/// <summary>
/// Favorites of the calling user, so the policy is <c>UserWithId</c> (never <c>AdminWithId</c>). The same
/// endpoint pair works from either surface: marking and unmarking are idempotent, and an identifier whose
/// game is not in the catalog is a 400 (the alternative is a foreign-key 500).
/// </summary>
[ApiController]
[Route("api/favorites")]
[Authorize(Policy = "UserWithId")]
public class FavoritesController : ControllerBase
{
    private readonly IFavoriteService _favoriteService;
    private readonly ICurrentUserService _currentUserService;

    public FavoritesController(IFavoriteService favoriteService, ICurrentUserService currentUserService)
    {
        _favoriteService = favoriteService;
        _currentUserService = currentUserService;
    }

    /// <summary>Marks the game as favorite. Idempotent: marking twice is still 204.</summary>
    [HttpPost]
    public async Task<IActionResult> Mark([FromBody] FavoriteRequest request, CancellationToken cancellationToken)
    {
        var applied = await ApplyAsync(request, favorite: true, cancellationToken);
        return applied ? NoContent() : BadRequest(new { Message = "El juego no está en el catálogo" });
    }

    /// <summary>Unmarks the game. Idempotent: unmarking something that was not marked is still 204.</summary>
    [HttpDelete]
    public async Task<IActionResult> Unmark([FromBody] FavoriteRequest request, CancellationToken cancellationToken)
    {
        var applied = await ApplyAsync(request, favorite: false, cancellationToken);
        return applied ? NoContent() : BadRequest(new { Message = "El juego no está en el catálogo" });
    }

    private Task<bool> ApplyAsync(FavoriteRequest? request, bool favorite, CancellationToken cancellationToken)
    {
        var userId = _currentUserService.GetRequiredUserId();

        // Un solo identificador: los dos a la vez son una petición ambigua, no una preferencia.
        var hasGameId = request?.GameId is > 0;
        var hasAppId = request?.SteamAppId is > 0;

        if ((hasGameId && hasAppId) || (!hasGameId && !hasAppId))
        {
            throw new ArgumentException("Envía gameId o steamAppId, no ambos ni ninguno", nameof(request));
        }

        return hasGameId
            ? _favoriteService.SetByGameIdAsync(userId, request!.GameId!.Value, favorite, cancellationToken)
            : _favoriteService.SetBySteamAppIdAsync(userId, request!.SteamAppId!.Value, favorite, cancellationToken);
    }
}
