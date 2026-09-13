using System.Security.Claims;

namespace Deals.API.Security;

public static class ClaimNames
{
    public const string Subject = "sub";
    public const string NameIdentifier = ClaimTypes.NameIdentifier;
    public const string Name = ClaimTypes.Name;
    public const string SessionVersion = "sessionVersion";
    public const string SessionId = "sid";
    public const string Role = ClaimTypes.Role;
    public const string AdminRole = "Admin";
}
