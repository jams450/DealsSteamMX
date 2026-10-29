using Deals.API.HostedServices;
using Deals.API.Interfaces;
using Deals.API.Security;
using Deals.API.Services;
using Deals.API.Services.Jobs;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Services;
using Deals.BusinessLogic.Services.Fx;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        // Per-scope observer of the sync diagnosis (spec S2): a job run and an interactive request each get their
        // own instance, which is what keeps two runs from sharing events and keeps request traffic out of a report.
        // A null logger is tolerated so a hand-built service (checks) needs no logging infrastructure.
        services.AddScoped<ISyncDiagnosticsCollector>(provider =>
            new SyncDiagnosticsCollector(provider.GetService<ILogger<SyncDiagnosticsCollector>>()));
        services.AddScoped<ISteamGameService, SteamGameService>();
        services.AddScoped<BundleCatalogService>();
        // The purchase writes a purchase snapshot, library rows, wishlist exclusions and canonical identity
        // in one transaction, so it is a scoped service with the shared identity resolver.
        services.AddScoped<IBundlePurchaseService, BundlePurchaseService>();
        services.AddOptions<GlobalBundleOptions>()
            .Validate(o => o.PageSize is > 0 and <= GlobalBundleOptions.MaxPageSize,
                "GlobalBundles:PageSize must be between 1 and 50.")
            .Validate(o => o.MaxPages is > 0 and <= 200, "GlobalBundles:MaxPages must be between 1 and 200.")
            .Validate(o => o.RefreshHours > 0, "GlobalBundles:RefreshHours must be greater than zero.")
            .Validate(o => o.JitterMinutes is >= 0 and <= 1440, "GlobalBundles:JitterMinutes must be between 0 and 1440.")
            .Validate(o => o.StartupDelaySeconds >= 0, "GlobalBundles:StartupDelaySeconds must not be negative.")
            .Validate(o => o.MinHoursBetweenRuns >= 0, "GlobalBundles:MinHoursBetweenRuns must not be negative.")
            .ValidateOnStart();
        services.AddSingleton(sp =>
        {
            var global = sp.GetRequiredService<IOptions<GlobalBundleOptions>>().Value;
            return new Deals.BusinessLogic.Models.Bundles.GlobalBundleSyncSettings(global.PageSize, global.MaxPages);
        });
        services.AddScoped<IGlobalBundleSnapshotWriter, EfGlobalBundleSnapshotWriter>();
        services.AddScoped<GlobalBundleSyncService>();
        services.AddHostedService<GlobalBundleSyncJob>();
        services.AddOptions<GgDealsBundleOptions>()
            .Validate(o => o.MaxPages is > 0 and <= 200, "GgDealsBundles:MaxPages must be between 1 and 200.")
            .Validate(o => o.CursorValidityMinutes is > 0 and <= 60, "GgDealsBundles:CursorValidityMinutes must be between 1 and 60.")
            .Validate(o => o.RefreshHours > 0, "GgDealsBundles:RefreshHours must be greater than zero.")
            .Validate(o => o.JitterMinutes is >= 0 and <= 1440, "GgDealsBundles:JitterMinutes must be between 0 and 1440.")
            .Validate(o => o.StartupDelaySeconds >= 0, "GgDealsBundles:StartupDelaySeconds must not be negative.")
            .Validate(o => o.MinHoursBetweenRuns >= 0, "GgDealsBundles:MinHoursBetweenRuns must not be negative.")
            .ValidateOnStart();
        services.AddScoped<IGgDealsBundleSnapshotWriter, EfGgDealsBundleSnapshotWriter>();
        services.AddScoped<GgDealsBundleSyncService>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GgDealsBundleOptions>>().Value;
            return new Deals.BusinessLogic.Models.GgDeals.GgDealsBundleSyncSettings(options.MaxPages, options.CursorValidityMinutes);
        });
        services.AddHostedService<GgDealsBundleSyncJob>();
        services.AddOptions<GlobalDealOptions>()
            .Validate(o => o.PageSize is > 0 and <= 200, "GlobalDeals:PageSize must be between 1 and 200.")
            .Validate(o => o.MaxPages is > 0 and <= 200, "GlobalDeals:MaxPages must be between 1 and 200.")
            .Validate(o => o.RefreshHours > 0, "GlobalDeals:RefreshHours must be positive.")
            .Validate(o => o.JitterMinutes is >= 0 and <= 1440, "GlobalDeals:JitterMinutes must be between 0 and 1440.")
            .Validate(o => o.StartupDelaySeconds >= 0 && o.MinHoursBetweenRuns >= 0, "GlobalDeals delays must not be negative.")
            .Validate(o => o.MinDiscountPercent is >= 0 and <= 100, "GlobalDeals:MinDiscountPercent must be between 0 and 100.")
            .ValidateOnStart();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GlobalDealOptions>>().Value;
            return new Deals.BusinessLogic.Models.Discovery.GlobalDealSyncSettings(options.PageSize, options.MaxPages);
        });
        services.AddSingleton(sp => new Deals.BusinessLogic.Models.Discovery.GlobalDealCatalogSettings(
            sp.GetRequiredService<IOptions<GlobalDealOptions>>().Value.MinDiscountPercent));
        services.AddScoped<IGlobalDealSnapshotWriter, EfGlobalDealSnapshotWriter>();
        services.AddScoped<GlobalDealSyncService>();
        services.AddScoped<GlobalDealCatalogService>();
        // Persisted discover catalog (spec S1/S2/S3/S6). Read-only over the same snapshots the notifier reads;
        // the caller passes the freshness/scoring context, so the service owns no clock and no configuration.
        // Its endpoint reads the clock through `TimeProvider`, registered here idempotently so this slice does
        // not depend on the notification extension being composed first.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<CatalogDealService>();
        // Price monitor of the requester's wishlist (spec S4): the same read-only, provider-free posture as the
        // catalog above, and a required wishlist-scope resolver so there is no unscoped path.
        services.AddScoped<ICatalogWishlistScopeResolver, CatalogWishlistScopeResolver>();
        services.AddScoped<CatalogFreshnessService>();
        services.AddHostedService<GlobalDealSyncJob>();
        services.AddScoped<IGameIdentityResolver, GameIdentityResolver>();
        services.AddScoped<IGameMergeService, GameMergeService>();
        services.AddScoped<ICrossStateReconciliationService, CrossStateReconciliationService>();
        services.AddScoped<IGameTitleEditService, GameTitleEditService>();
        services.AddScoped<ILibraryPriceBindingService, LibraryPriceBindingService>();
        services.AddScoped<IGameOwnershipService, GameOwnershipService>();
        services.AddScoped<ISteamGameUserStateService, SteamGameUserStateService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<IFavoriteService, FavoriteService>();
        services.AddScoped<IGameTierService, GameTierService>();
    services.AddScoped<IManualLibraryService, ManualLibraryService>();
    services.AddScoped<IConsoleLibraryImportService, ConsoleLibraryImportService>();
    services.AddHttpClient<IManualSearchService, ManualSearchService>();

    // SteamGridDB is the last source of the automatic cover chain (docs/PLAN_LIBRARY.md §9). Sequential and
    // never retried on purpose: the provider documents no rate limit, so the pass stays one bounded request at
    // a time instead of designing against a limit nobody published. The key is a flat env setting read inside
    // the client, never a URL segment.
    services.AddOptions<SteamGridDbOptions>()
        .Validate(
            options => IsHttps(options.BaseUrl),
            "SteamGridDb:BaseUrl must be an absolute HTTPS URL.")
        .Validate(
            options => options.TimeoutSeconds is > 0 and <= 60,
            "SteamGridDb:TimeoutSeconds must be between 1 and 60.")
        .ValidateOnStart();

    services.AddHttpClient<ISteamGridDbClient, SteamGridDbClient>((serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<SteamGridDbOptions>>().Value;
        if (!IsHttps(options.BaseUrl))
        {
            throw new InvalidOperationException("SteamGridDb:BaseUrl must be an absolute HTTPS URL.");
        }

        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
    });

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
                options => !SecretPlaceholder.IsPlaceholder(options.ApiKey),
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

        services.AddSingleton(serviceProvider =>
        {
            var budget = serviceProvider.GetRequiredService<IOptions<JobBudgetOptions>>().Value.Itad;
            return new ItadRequestGovernor(
                budget.RequestsPerFiveMinutes,
                budget.MaxBurst,
                budget.MinDelayMilliseconds,
                serviceProvider.GetRequiredService<ILogger<ItadRequestGovernor>>());
        });
        services.AddSingleton(serviceProvider =>
        {
            var budget = serviceProvider.GetRequiredService<IOptions<JobBudgetOptions>>().Value.GgDeals;
            return new GgDealsRequestGovernor(budget.RecordsPerMinute, budget.RecordsPerHour, budget.MaxBurstRecords, budget.MinDelayMilliseconds);
        });

        // Separate process-wide policies shared by foreground and background HTTP attempts.
        services.AddSingleton(serviceProvider =>
        {
            var budget = serviceProvider.GetRequiredService<IOptions<JobBudgetOptions>>().Value.Epic;
            return new EpicRequestGovernor(budget.RequestsPerMinute, budget.RequestsPerHour, budget.MinDelayMilliseconds);
        });
        services.AddSingleton(serviceProvider =>
        {
            var budget = serviceProvider.GetRequiredService<IOptions<JobBudgetOptions>>().Value.Microsoft;
            return new MicrosoftRequestGovernor(budget.RequestsPerMinute, budget.RequestsPerHour, budget.MinDelayMilliseconds);
        });

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

        services.AddHttpClient<IGlobalDealClient, ItadGlobalDealClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ItadOptions>>().Value;
            if (!IsHttps(options.BaseUrl))
                throw new InvalidOperationException("ITAD:BaseUrl must be an absolute HTTPS URL.");
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
        });

        services.AddOptions<GgDealsOptions>()
            .Validate(
                options => IsHttps(options.BaseUrl),
                "GgDeals:BaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => !SecretPlaceholder.IsPlaceholder(options.ApiKey),
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

        services.AddOptions<MicrosoftOptions>()
            .Validate(
                options => IsHttps(options.BaseUrl),
                "Microsoft:BaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => options.Market?.Length == 2 && options.Market.All(char.IsAsciiLetterUpper),
                "Microsoft:Market must be a two-letter uppercase country code.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Languages) && options.Languages.Length <= 32,
                "Microsoft:Languages must be a non-empty language list.")
            .Validate(
                options => options.Currency?.Length == 3 && options.Currency.All(char.IsAsciiLetterUpper),
                "Microsoft:Currency must be a three-letter uppercase currency code.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.UserAgent) && options.UserAgent.Length <= 256,
                "Microsoft:UserAgent must be configured.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<MicrosoftOptions>>().Value;
            return new MicrosoftStoreClientSettings(options.Market, options.Languages, options.Currency);
        });

        // Deliberately NOT mapped onto IStorePriceProvider. SteamGameService resolves that interface for its
        // Epic phase, and .NET DI hands a single-instance request the LAST registration: an unkeyed mapping
        // here would make every Epic refresh call the Microsoft client with an Epic slug, quietly returning
        // no offer and purging the Epic rows. Consumers of this store ask for MicrosoftStoreClient by name,
        // which is also the right shape for a pass that only Microsoft ids can drive.
        services.AddHttpClient<MicrosoftStoreClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<MicrosoftOptions>>().Value;
            if (!IsHttps(options.BaseUrl))
            {
                throw new InvalidOperationException("Microsoft:BaseUrl must be an absolute HTTPS URL.");
            }

            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        });

        services.AddScoped<ILibraryStorePriceService, LibraryStorePriceService>();

        services.AddOptions<FxOptions>()
            .Validate(
                options => IsHttps(options.BaseUrl),
                "FX:BaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => !SecretPlaceholder.IsPlaceholder(options.BanxicoToken),
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
            .Validate(
                options => options.MissingMetadataDelayMilliseconds >= 0 && options.MissingMetadataDelayMilliseconds <= 60_000,
                "Wishlist:MissingMetadataDelayMilliseconds must be between 0 and 60000.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WishlistOptions>>().Value;
            var itadOptions = serviceProvider.GetRequiredService<IOptions<ItadOptions>>().Value;
            return new WishlistSyncSettings(options.MaxRefreshesPerHour, itadOptions.RefreshAfterDays, options.MissingMetadataDelayMilliseconds);
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
        // The acquisition writes a library row, an exclusion and the canonical mapping in one transaction, so
        // it is a scoped service with the shared identity resolver, not a controller helper.
        services.AddScoped<IWishlistAcquisitionService, WishlistAcquisitionService>();
        services.AddScoped<JobRunLog>();

        // --- Admin jobs: catalog, history and manual dispatch (spec S2) ---------------------------------
        // The catalog is a pure value built from the same configuration the hosted services read, so it can
        // say "this job is disabled and why" without touching DI per request.
        services.AddScoped<IJobRunLogQueryService, JobRunLogQueryService>();
        services.AddSingleton(serviceProvider =>
        {
            var wishlist = serviceProvider.GetRequiredService<IOptions<WishlistOptions>>().Value;
            var globalBundles = serviceProvider.GetRequiredService<IOptions<GlobalBundleOptions>>().Value;
            var ggDealsBundles = serviceProvider.GetRequiredService<IOptions<GgDealsBundleOptions>>().Value;
            var globalDeals = serviceProvider.GetRequiredService<IOptions<GlobalDealOptions>>().Value;
            var ntfy = serviceProvider.GetRequiredService<IOptions<NtfyOptions>>().Value;
            var fx = serviceProvider.GetRequiredService<IOptions<FxOptions>>().Value;
            var budget = serviceProvider.GetRequiredService<IOptions<JobBudgetOptions>>().Value;

            return new JobCatalogSettings(
                wishlist.Enabled,
                wishlist.RunAtHour,
                wishlist.TimeZoneId,
                globalBundles.Enabled,
                globalBundles.RefreshHours,
                ggDealsBundles.Enabled,
                ggDealsBundles.RefreshHours,
                globalDeals.Enabled,
                globalDeals.RefreshHours,
                ntfy.Enabled,
                ntfy.PollSeconds,
                fx.BaseCurrency,
                fx.QuoteCurrency,
                budget.RetentionDays);
        });
        services.AddSingleton<JobCatalog>();

        // One queue and one dispatcher per process: the request only records and queues, and the hosted
        // consumer performs the work with the same lock and the same job_runs row as the scheduled cycle.
        services.AddSingleton<IJobRunQueue, JobRunQueue>();
        services.AddScoped<IJobRunTracker, JobRunTracker>();
        services.AddScoped<JobSubmissionService>();
        services.AddScoped<JobExecutionRunner>();

        // The work of each job. Both the hosted service and the dispatcher resolve these, so a manual run is
        // never a second implementation of the same pass.
        // Scheduled jobs request concrete types; manual dispatch enumerates IJobExecutor. Register both
        // paths against the same scoped instance, otherwise scheduled runs fail before contacting providers.
        services.AddScoped<WishlistSyncJobExecutor>();
        services.AddScoped<IJobExecutor>(sp => sp.GetRequiredService<WishlistSyncJobExecutor>());
        services.AddScoped<FxRateRefreshJobExecutor>();
        services.AddScoped<IJobExecutor>(sp => sp.GetRequiredService<FxRateRefreshJobExecutor>());
        services.AddScoped<GlobalBundleSyncJobExecutor>();
        services.AddScoped<IJobExecutor>(sp => sp.GetRequiredService<GlobalBundleSyncJobExecutor>());
        services.AddScoped<GgDealsBundleSyncJobExecutor>();
        services.AddScoped<IJobExecutor>(sp => sp.GetRequiredService<GgDealsBundleSyncJobExecutor>());
        services.AddScoped<GlobalDealSyncJobExecutor>();
        services.AddScoped<IJobExecutor>(sp => sp.GetRequiredService<GlobalDealSyncJobExecutor>());
        services.AddScoped<NtfyDealsJobExecutor>();
        services.AddScoped<IJobExecutor>(sp => sp.GetRequiredService<NtfyDealsJobExecutor>());
        services.AddScoped<JobRunPurgeJobExecutor>();
        services.AddScoped<IJobExecutor>(sp => sp.GetRequiredService<JobRunPurgeJobExecutor>());

        // Manual-only pending-price pass (spec S5/S6): registered for the dispatcher, deliberately NOT as a
        // hosted service — nothing in the host schedules it. Its candidate selection, its 72 h provider gates
        // and its wishlist scope (the requester's own list, captured when queued) live in the business layer;
        // the executor only maps counters to a job result.
        services.AddScoped<ICatalogPriceRefreshService, CatalogPriceRefreshService>();
        services.AddScoped<CatalogPriceRefreshJobExecutor>();
        services.AddScoped<IJobExecutor>(sp => sp.GetRequiredService<CatalogPriceRefreshJobExecutor>());
        services.AddHostedService<JobRunDispatchService>();
        services.AddOptions<JobBudgetOptions>()
            .Validate(options => options.RetentionDays > 0 && options.PurgeBatchSize > 0, "JobBudgets retention and purge batch must be positive.")
            .Validate(options => options.Itad.RequestsPerFiveMinutes > 0 && options.Itad.MaxBurst > 0 && options.Itad.MinDelayMilliseconds >= 0, "JobBudgets:Itad values are invalid.")
            .Validate(options => options.GgDeals.RecordsPerMinute > 0 && options.GgDeals.RecordsPerHour > 0 && options.GgDeals.MaxBurstRecords > 0 && options.GgDeals.MaxBurstRecords <= 100 && options.GgDeals.MaxBurstRecords <= options.GgDeals.RecordsPerMinute && options.GgDeals.MaxBurstRecords <= options.GgDeals.RecordsPerHour && options.GgDeals.MinDelayMilliseconds >= 0, "JobBudgets:GgDeals values are invalid.")
            .Validate(options => options.Epic is { IsValid: true }, "JobBudgets:Epic requires positive request limits and nonnegative delay.")
            .Validate(options => options.Microsoft is { IsValid: true }, "JobBudgets:Microsoft requires positive request limits and nonnegative delay.")
            .ValidateOnStart();
        services.AddHostedService<WishlistSyncJob>();
        services.AddHostedService<JobRunPurgeJob>();

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
