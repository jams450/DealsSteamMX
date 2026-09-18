using System.Threading.RateLimiting;
using Deals.API.Security;
using Microsoft.AspNetCore.RateLimiting;

namespace Deals.API.Extensions;

public static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            options.AddPolicy("steam-read", context => RateLimitPartition.GetFixedWindowLimiter(
                GetClientKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            options.AddPolicy("steam-refresh", context => RateLimitPartition.GetFixedWindowLimiter(
                GetClientKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 6,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });

        return services;
    }

    // Prefijos "user:" / "ip:" son distintos a propósito: un claim "1.2.3.4" nunca comparte
    // partición con la IP 1.2.3.4. No se registra el valor del claim.
    // Program.cs llama UseAuthentication() antes de UseRateLimiter(), así que context.User ya trae
    // claims cuando corre la política: las peticiones autenticadas se limitan por usuario y las
    // anónimas (o sin el claim) caen en "ip:".
    private static string GetClientKey(HttpContext context)
    {
        var userId = context.User.FindFirst(ClaimNames.NameIdentifier)?.Value;

        return string.IsNullOrWhiteSpace(userId)
            ? $"ip:{GetClientIp(context)}"
            : $"user:{userId}";
    }

    private static string GetClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
