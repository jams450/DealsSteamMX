using System.ComponentModel.DataAnnotations;

namespace Deals.API.Models.Users;

public class UserUpdateRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(100)]
    public string Email { get; set; } = string.Empty;

    [StringLength(255)]
    public string? Password { get; set; }

    public bool Active { get; set; } = true;
    public bool Admin { get; set; } = false;

    [StringLength(20)]
    public string? SteamId64 { get; set; }
}
