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
        services.AddScoped<IGameIdentityResolver, GameIdentityResolver>();
        services.AddScoped<IGameMergeService, GameMergeService>();
        services.AddScoped<ILibraryPriceBindingService, LibraryPriceBindingService>();
        services.AddScoped<IGameOwnershipService, GameOwnershipService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<IFavoriteService, FavoriteService>();
        services.AddScoped<ILibraryCoverService, LibraryCoverService>();
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

        // Shared in-process provider budget (1 req/s, burst 10, no queueing) across every price provider and request.
        services.AddSingleton<ProviderRequestGovernor>();

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

        services.AddOptions<GgDealsOptions>()
            .Validate(
                options => IsHttps(options.BaseUrl),
                "GgDeals:BaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ApiKey) &&
                           !options.ApiKey.StartsWith("SET_", StringComparison.OrdinalIgnoreCase),
                "GgDeals:ApiKey must be configured with a non-placeholder value.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<GgDealsOptions>>().Value;
            return new GgDealsClientSettings(options.ApiKey, options.Region);
        });

        services.AddHttpClient<IGgDealsClient, GgDealsClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<GgDealsOptions>>().Value;
            if (!IsHttps(options.BaseUrl))
            {
                throw new InvalidOperationException("GgDeals:BaseUrl must be an absolute HTTPS URL.");
            }

            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
        });

        services.AddOptions<EpicOptions>()
            .Validate(
                options => IsHttps(options.BaseUrl),
                "Epic:BaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => options.Country?.Length == 2 && options.Country.All(char.IsAsciiLetterUpper),
                "Epic:Country must be a two-letter uppercase country code.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Locale) && options.Locale.Length <= 16,
                "Epic:Locale must be a non-empty store locale.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.UserAgent) && options.UserAgent.Length <= 256,
                "Epic:UserAgent must be configured: the store answers 403 without one.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<EpicOptions>>().Value;
            return new EpicStoreClientSettings(options.Country, options.Locale);
        });

        // Typed registration plus an explicit interface mapping, so a second store provider can be added by
        // repeating these two lines instead of silently replacing this one: registering "IStorePriceProvider"
        // directly would make the last store win for every single-provider consumer.
        services.AddHttpClient<EpicStoreClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<EpicOptions>>().Value;
            if (!IsHttps(options.BaseUrl))
            {
                throw new InvalidOperationException("Epic:BaseUrl must be an absolute HTTPS URL.");
            }

            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
            // Not cosmetic: the store answers 403 when no User-Agent is sent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });
        services.AddScoped<IStorePriceProvider>(serviceProvider => serviceProvider.GetRequiredService<EpicStoreClient>());

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

        services.AddOptions<WishlistOptions>()
            .Validate(
                options => IsHttps(options.ApiBaseUrl),
                "Wishlist:ApiBaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => options.RunAtHour is >= 0 and <= 23,
                "Wishlist:RunAtHour must be between 0 and 23.")
            .Validate(
                options => options.JitterMinutes >= 0,
                "Wishlist:JitterMinutes must not be negative.")
            .Validate(
                options => ValidateWishlistTimeZone(options.TimeZoneId),
                "Wishlist:TimeZoneId no es resoluble o está vacío.")
            .Validate(
                options => options.StartupDelaySeconds >= 0,
                "Wishlist:StartupDelaySeconds must not be negative.")
            .Validate(
                options => options.MaxRefreshesPerHour > 0,
                "Wishlist:MaxRefreshesPerHour must be greater than zero.")
            .Validate(
                options => options.MinHoursBetweenRuns >= 0,
                "Wishlist:MinHoursBetweenRuns must not be negative.")
            .Validate(
                options => options.TimeoutSeconds > 0,
                "Wishlist:TimeoutSeconds must be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WishlistOptions>>().Value;
            var itadOptions = serviceProvider.GetRequiredService<IOptions<ItadOptions>>().Value;
            return new WishlistSyncSettings(options.MaxRefreshesPerHour, itadOptions.RefreshAfterDays);
        });

        services.AddHttpClient<ISteamWishlistClient, SteamWishlistClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WishlistOptions>>().Value;
            if (!IsHttps(options.ApiBaseUrl))
            {
                throw new InvalidOperationException("Wishlist:ApiBaseUrl must be an absolute HTTPS URL.");
            }

            client.BaseAddress = new Uri(options.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
        });

        services.AddScoped<IWishlistSyncService, WishlistSyncService>();
        services.AddScoped<JobRunLog>();
        services.AddHostedService<WishlistSyncJob>();

        return services;
    }

    private static IReadOnlySet<string> ParseShopIds(string officialShopIds) =>
        officialShopIds
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool ValidateWishlistTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
