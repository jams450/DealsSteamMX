namespace Deals.API.Interfaces;

public interface IJwtService
{
    string GenerateToken(int userId, string username, bool isAdmin = false, int sessionVersion = 1, Guid? sessionId = null);
    DateTime GetTokenExpiration();
}
