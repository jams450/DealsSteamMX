namespace Deals.API.Extensions;

public static class CorsExtensions
{
    public static IServiceCollection AddApiCors(this IServiceCollection services, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()?
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(origin => origin.Trim())
            .ToArray();

        if (allowedOrigins is null || allowedOrigins.Length == 0)
        {
            var allowedOriginsRaw = configuration["Cors:AllowedOrigins"];
            allowedOrigins = string.IsNullOrWhiteSpace(allowedOriginsRaw)
                ? Array.Empty<string>()
                : allowedOriginsRaw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (allowedOrigins.Length == 0)
        {
            // Safe development fallback to avoid hard crash when config is missing.
            allowedOrigins = ["http://localhost:3000"];
        }

        services.AddCors(options =>
        {
            options.AddPolicy("Production", policy =>
            {
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });

        return services;
    }
}
