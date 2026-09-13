using System.ComponentModel.DataAnnotations;

namespace Deals.API.Models.Users;

public class UserCreateRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(100)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(255, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    public bool Active { get; set; } = true;
    public bool Admin { get; set; } = false;
}
