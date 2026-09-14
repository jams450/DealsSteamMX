using Deals.API.Interfaces;
using Deals.API.Services;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Services;
using Microsoft.Extensions.Options;
using IPasswordService = Deals.BusinessLogic.Interfaces.IPasswordService;
using PasswordService = Deals.BusinessLogic.Services.PasswordService;

namespace Deals.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPasswordService, PasswordService>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ISteamGameService, SteamGameService>();
        services.AddScoped<IRepository, Repository>();
        services.AddHttpClient<ISteamStoreClient, SteamStoreClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<SteamOptions>>().Value;
            if (!Uri.TryCreate(options.StoreBaseUrl, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("Steam:StoreBaseUrl must be an absolute HTTPS URL.");
            }

            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
        });

        return services;
    }
}
