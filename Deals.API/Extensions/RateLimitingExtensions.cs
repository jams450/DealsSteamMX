using System.Text.Json;
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
            // A rate limit is not a server failure: without this the middleware replies 503 and clients
            // cannot tell "wait a minute" from "the API is down".
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.Response.ContentType = "application/problem+json";
                await JsonSerializer.SerializeAsync(
                    context.HttpContext.Response.Body,
                    new { code = "RATE_LIMITED", message = "Rate limit exceeded; retry after one minute." },
                    cancellationToken: cancellationToken);
            };

            // Login y refresh llevan presupuestos separados a propósito. La partición es la misma (la IP del
            // par; en docker-compose, el contenedor del BFF), así que un cubo único permitía que diez POST de
            // login por minuto agotaran también el refresh y expulsaran a todos los usuarios con
            // SESSION_EXPIRED. Residuo conocido: cada cubo sigue siendo compartido mientras la API no vea la
            // dirección real del cliente; eso lo habilita UseApiForwardedHeaders con Proxy:KnownProxies.
            options.AddPolicy("auth-login", context => RateLimitPartition.GetFixedWindowLimiter(
                GetClientIp(context),
                _ => AuthWindow()));

            options.AddPolicy("auth-refresh", context => RateLimitPartition.GetFixedWindowLimiter(
                GetClientIp(context),
                _ => AuthWindow()));

            options.AddPolicy("steam-read", context => RateLimitPartition.GetFixedWindowLimiter(
                GetClientKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            options.AddPolicy("wishlist-package-preview", context => RateLimitPartition.GetFixedWindowLimiter(
                GetClientKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            // Una ejecución manual consume presupuesto de proveedor y puede durar minutos: seis por minuto
            // y por administrador es el techo operativo, no una protección contra ráfagas anónimas (la ruta
            // ya es AdminWithId). El duplicado real lo corta la cola, no este límite.
            options.AddPolicy("jobs-dispatch", context => RateLimitPartition.GetFixedWindowLimiter(
                GetClientKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 6,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

            options.AddPolicy("steam-refresh", context => RateLimitPartition.GetFixedWindowLimiter(
                GetClientIp(context),
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

    // Cada partición necesita sus propias opciones: el limitador las conserva por clave.
    private static FixedWindowRateLimiterOptions AuthWindow() => new()
    {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
    };

    private static string GetClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
