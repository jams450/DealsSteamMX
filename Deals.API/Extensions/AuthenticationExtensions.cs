using System.Text;
using Deals.API.Security;
using Deals.BusinessLogic.Context;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Deals.API.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                ValidateJwtKey(jwt.Key);

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                    ClockSkew = TimeSpan.Zero
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var userIdClaim = context.Principal?.FindFirst(ClaimNames.NameIdentifier)?.Value
                            ?? context.Principal?.FindFirst(ClaimNames.Subject)?.Value;

                        if (!int.TryParse(userIdClaim, out var userId) || userId <= 0)
                        {
                            context.Fail("Invalid user id claim.");
                            return;
                        }

                        var sessionVersionClaim = context.Principal?.FindFirst(ClaimNames.SessionVersion)?.Value;
                        if (!int.TryParse(sessionVersionClaim, out var tokenSessionVersion))
                        {
                            context.Fail("Missing or invalid sessionVersion claim.");
                            return;
                        }

                        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);

                        if (user == null || !user.Active || (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow))
                        {
                            context.Fail("User is inactive or locked.");
                            return;
                        }

                        if (user.SessionVersion != tokenSessionVersion)
                        {
                            context.Fail("Token session version is no longer valid.");
                            return;
                        }

                        var sidClaim = context.Principal?.FindFirst(ClaimNames.SessionId)?.Value;
                        if (!Guid.TryParse(sidClaim, out var sessionId))
                        {
                            context.Fail("Missing or invalid session id claim.");
                            return;
                        }

                        var session = await db.UserSessions.AsNoTracking()
                            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);

                        if (session == null || session.RevokedAt.HasValue || session.ExpiresAt <= DateTime.UtcNow)
                        {
                            context.Fail("Session is revoked or expired.");
                        }
                    }
                };
            });

        return services;
    }

    private static void ValidateJwtKey(string jwtKey)
    {
        if (string.IsNullOrWhiteSpace(jwtKey) ||
            jwtKey.StartsWith("SET_", StringComparison.OrdinalIgnoreCase) ||
            Encoding.UTF8.GetByteCount(jwtKey) < 32)
        {
            throw new InvalidOperationException(
                "Jwt:Key must be configured with a non-placeholder value of at least 32 UTF-8 bytes.");
        }
    }
}
