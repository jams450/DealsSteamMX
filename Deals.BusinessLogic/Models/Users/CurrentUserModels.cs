namespace Deals.BusinessLogic.Models.Users;

public record CurrentUserProfile(int UserId, string Name, string Email, bool Admin);

public record CurrentUserSession(
    Guid SessionId,
    DateTime? Created,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    string? Ip,
    string? UserAgent);

public enum ChangeOwnPasswordResult
{
    Success,
    UserNotFound,
    InvalidCurrentPassword
}
