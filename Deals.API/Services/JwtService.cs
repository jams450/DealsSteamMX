using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Deals.API.Interfaces;
using Deals.API.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Deals.API.Services;

public class JwtService : IJwtService
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _securityKey;

    public JwtService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        _securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
    }

    public string GenerateToken(int userId, string username, bool isAdmin = false, int sessionVersion = 1, Guid? sessionId = null)
    {
        var credentials = new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(ClaimNames.Subject, userId.ToString()),
            new(ClaimNames.NameIdentifier, userId.ToString()),
            new(ClaimNames.Name, username),
            new(ClaimNames.SessionVersion, sessionVersion.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (sessionId.HasValue)
        {
            claims.Add(new Claim(ClaimNames.SessionId, sessionId.Value.ToString()));
        }

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimNames.Role, ClaimNames.AdminRole));
        }

        var token = new JwtSecurityToken(_options.Issuer, _options.Audience, claims, expires: GetTokenExpiration(), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public DateTime GetTokenExpiration() => DateTime.UtcNow.AddHours(Math.Max(1, _options.ExpirationHours));
}
