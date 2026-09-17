using Deals.API.HostedServices;
using Deals.API.Interfaces;
using Deals.API.Services;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Services;
using Deals.BusinessLogic.Services.Fx;
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

        services.AddOptions<ItadOptions>()
            .Validate(
                options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) &&
                           baseUri.Scheme == Uri.UriSchemeHttps,
                "ITAD:BaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => ParseShopIds(options.OfficialShopIds) is { Count: > 0 } shopIds &&
                           shopIds.All(id => int.TryParse(id, out var value) && value > 0),
                "ITAD:OfficialShopIds must contain at least one positive numeric shop id.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ApiKey) &&
                           !options.ApiKey.StartsWith("SET_", StringComparison.OrdinalIgnoreCase),
                "ITAD:ApiKey must be configured with a non-placeholder value.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ItadOptions>>().Value;
            return new ItadClientSettings(options.ApiKey, options.Country, ParseShopIds(options.OfficialShopIds));
        });

        // Offer cache window comes from configuration instead of falling back to the service default.
        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ItadOptions>>().Value;
            return new SteamOffersSettings(options.RefreshAfterDays);
        });

        // Shared in-process ITAD budget (1 req/s, burst 10, no queueing) across all clients and requests.
        services.AddSingleton<ItadRequestGovernor>();

        services.AddHttpClient<IItadClient, ItadClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ItadOptions>>().Value;
            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("ITAD:BaseUrl must be an absolute HTTPS URL.");
            }

            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
        });

        services.AddOptions<FxOptions>()
            .Validate(
                options => IsHttps(options.BaseUrl),
                "FX:BaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.BanxicoToken) &&
                           !options.BanxicoToken.StartsWith("SET_", StringComparison.OrdinalIgnoreCase),
                "FX:BanxicoToken must be configured with a non-placeholder value.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<FxOptions>>().Value;
            return new BanxicoFxRateSettings(options.BanxicoToken);
        });

        services.AddHttpClient<BanxicoFxRateProvider>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<FxOptions>>().Value;
            if (!IsHttps(options.BaseUrl))
            {
                throw new InvalidOperationException("FX:BaseUrl must be an absolute HTTPS URL.");
            }

            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
        });

        services.AddHttpClient<FrankfurterFxRateProvider>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<FxOptions>>().Value;
            client.BaseAddress = new Uri(FrankfurterFxRateProvider.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
        });

        services.AddScoped<IFxRateService, FxRateService>();
        services.AddHostedService<FxRateRefreshJob>();

        return services;
    }

    private static IReadOnlySet<string> ParseShopIds(string officialShopIds) =>
        officialShopIds
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
