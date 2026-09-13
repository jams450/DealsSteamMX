using Deals.API.Models.Users;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Deals.API.Controllers;

[ApiController]
[Route("api/me")]
[Authorize(Policy = "UserWithId")]
public class MeController : ControllerBase
{
    private readonly IUserService _userService;

    public MeController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpGet]
    public async Task<IActionResult> GetProfile()
    {
        var profile = await _userService.GetCurrentProfileAsync(GetUserId());
        return profile == null ? NotFound() : Ok(ToResponse(profile));
    }

    [HttpPut]
    public async Task<IActionResult> UpdateProfile([FromBody] MeProfileUpdateRequest request)
    {
        try
        {
            var profile = await _userService.UpdateCurrentProfileAsync(GetUserId(), request.Name, request.Email);
            return profile == null ? NotFound() : Ok(ToResponse(profile));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] MePasswordChangeRequest request)
    {
        try
        {
            var result = await _userService.ChangeOwnPasswordAsync(GetUserId(), request.CurrentPassword, request.NewPassword);
            return result switch
            {
                ChangeOwnPasswordResult.Success => NoContent(),
                ChangeOwnPasswordResult.InvalidCurrentPassword => BadRequest(new { Message = "Current password is invalid" }),
                _ => NotFound()
            };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var currentSessionId = GetSessionId();
        var sessions = await _userService.GetSessionsAsync(GetUserId());
        return Ok(sessions.Select(s => new MeSessionResponse(
            s.SessionId, s.Created, s.ExpiresAt, s.RevokedAt, s.Ip, s.UserAgent,
            s.SessionId == currentSessionId)));
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> RevokeSession(Guid sessionId)
    {
        return await _userService.RevokeSessionAsync(GetUserId(), sessionId) ? NoContent() : NotFound();
    }

    [HttpPost("sessions/revoke-others")]
    public async Task<IActionResult> RevokeOtherSessions()
    {
        await _userService.RevokeOtherSessionsAsync(GetUserId(), GetSessionId());
        return NoContent();
    }

    [HttpPost("sessions/revoke-all")]
    public async Task<IActionResult> RevokeAllSessions()
    {
        return await _userService.RevokeAllSessionsAsync(GetUserId()) ? NoContent() : NotFound();
    }

    private int GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(value, out var userId) && userId > 0
            ? userId
            : throw new UnauthorizedAccessException("Missing or invalid user identity claim");
    }

    private Guid GetSessionId()
    {
        var value = User.FindFirstValue("sid");
        return Guid.TryParse(value, out var sessionId)
            ? sessionId
            : throw new UnauthorizedAccessException("Missing or invalid session id claim");
    }

    private static MeProfileResponse ToResponse(CurrentUserProfile profile) =>
        new(profile.UserId, profile.Name, profile.Email, profile.Admin);
}
