namespace Deals.API.Models.Auth;

public record LoginResponse(
    string Token,
    DateTime Expiration,
    string Username,
    string? RefreshToken,
    DateTime? RefreshTokenExpiration);
