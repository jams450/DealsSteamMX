using System.ComponentModel.DataAnnotations;

namespace Deals.API.Models.Users;

public class MeProfileUpdateRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(100)]
    public string Email { get; set; } = string.Empty;
}

public class MePasswordChangeRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    public string NewPassword { get; set; } = string.Empty;
}

public record MeProfileResponse(int UserId, string Name, string Email, bool Admin);

public record MeSessionResponse(
    Guid SessionId,
    DateTime? Created,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    string? Ip,
    string? UserAgent,
    bool IsCurrent);
