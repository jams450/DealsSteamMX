using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Deals.API.Models.Users;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "AdminWithId")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUserService userService, ILogger<UsersController> logger)
    {
        _userService = userService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        try
        {
            var users = await _userService.GetAllAsync();
            return Ok(users.Adapt<IEnumerable<UserResponse>>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving users");
            return StatusCode(500, new { Message = "An error occurred while retrieving users" });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        try
        {
            var user = await _userService.GetByIdAsync(id);
            if (user == null)
                return NotFound(new { Message = $"User with ID {id} not found" });

            return Ok(user.Adapt<UserResponse>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user with ID {Id}", id);
            return StatusCode(500, new { Message = "An error occurred while retrieving the user" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserCreateRequest request)
    {
        try
        {
            var user = request.Adapt<User>();
            var createdUser = await _userService.CreateAsync(user);
            _logger.LogInformation("User created successfully: {Email}", createdUser.Email);

            return CreatedAtAction(nameof(GetById), new { id = createdUser.UserId }, createdUser.Adapt<UserResponse>());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user");
            return StatusCode(500, new { Message = "An error occurred while creating the user" });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UserUpdateRequest request)
    {
        try
        {
            var existingUser = await _userService.GetByIdAsync(id);
            if (existingUser == null)
                return NotFound(new { Message = $"User with ID {id} not found" });

            var user = request.Adapt<User>();
            user.UserId = id;
            var updatedUser = await _userService.UpdateAsync(id, user);
            if (updatedUser == null)
            {
                return NotFound(new { Message = $"User with ID {id} not found" });
            }

            _logger.LogInformation("User updated successfully: {Email}", updatedUser?.Email);

            return Ok(updatedUser.Adapt<UserResponse>());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user with ID {Id}", id);
            return StatusCode(500, new { Message = "An error occurred while updating the user" });
        }
    }

    [HttpPatch("{id}/active")]
    public async Task<IActionResult> UpdateActiveStatus(int id, [FromBody] bool active)
    {
        try
        {
            var existingUser = await _userService.GetByIdAsync(id);
            if (existingUser == null)
                return NotFound(new { Message = $"User with ID {id} not found" });

            var result = await _userService.UpdateActiveStatusAsync(id, active);
            if (!result)
                return StatusCode(500, new { Message = "Failed to update user status" });

            _logger.LogInformation("User {Id} active status updated to {Active}", id, active);
            return Ok(new { Message = $"User active status updated to {active}" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating active status for user with ID {Id}", id);
            return StatusCode(500, new { Message = "An error occurred while updating the user status" });
        }
    }

    [HttpPatch("{id}/admin")]
    public async Task<IActionResult> UpdateAdminStatus(int id, [FromBody] UserUpdateAdminRequest request)
    {
        try
        {
            var existingUser = await _userService.GetByIdAsync(id);
            if (existingUser == null)
            {
                return NotFound(new { Message = $"User with ID {id} not found" });
            }

            var result = await _userService.UpdateAdminStatusAsync(id, request.Admin);
            if (!result)
            {
                return StatusCode(500, new { Message = "Failed to update admin status" });
            }

            _logger.LogInformation("User {Id} admin status updated to {Admin}", id, request.Admin);
            return Ok(new { Message = $"User admin status updated to {request.Admin}" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating admin status for user with ID {Id}", id);
            return StatusCode(500, new { Message = "An error occurred while updating the admin status" });
        }
    }

    [HttpPatch("{id}/password")]
    public async Task<IActionResult> ChangePassword(int id, [FromBody] UserChangePasswordRequest request)
    {
        try
        {
            var existingUser = await _userService.GetByIdAsync(id);
            if (existingUser == null)
            {
                return NotFound(new { Message = $"User with ID {id} not found" });
            }

            var result = await _userService.ChangePasswordAsync(id, request.NewPassword);
            if (!result)
            {
                return StatusCode(500, new { Message = "Failed to change user password" });
            }

            _logger.LogInformation("User password changed successfully: {Id}", id);
            return Ok(new { Message = "User password changed successfully" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password for user with ID {Id}", id);
            return StatusCode(500, new { Message = "An error occurred while changing the user password" });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var existingUser = await _userService.GetByIdAsync(id);
            if (existingUser == null)
                return NotFound(new { Message = $"User with ID {id} not found" });

            var result = await _userService.DeleteAsync(id);
            if (!result)
                return StatusCode(500, new { Message = "Failed to delete user" });

            _logger.LogInformation("User deleted successfully: {Id}", id);
            return Ok(new { Message = "User deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user with ID {Id}", id);
            return StatusCode(500, new { Message = "An error occurred while deleting the user" });
        }
    }
}
