using Deals.API.HostedServices;
using Deals.API.Security;
using Deals.API.Services.Notifications;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Deals.API.Extensions;

/// <summary>
/// Registration for the global ntfy deal alerts. Kept in one extension so the composition root only adds
/// one call: <c>builder.Services.AddApiNtfyNotifications()</c>, after
/// <c>builder.Services.Configure&lt;NtfyOptions&gt;(builder.Configuration.GetSection(NtfyOptions.SectionName))</c>.
///
/// Nothing here runs when <see cref="NtfyOptions.Enabled"/> is false: the options validation is
/// conditional, and the hosted job returns without touching the outbox.
/// </summary>
public static class NtfyServiceCollectionExtensions
{
    public static IServiceCollection AddApiNtfyNotifications(this IServiceCollection services)
    {
        services.AddOptions<NtfyOptions>()
            .Validate(
                options => !options.Enabled || IsHttps(options.BaseUrl),
                "Ntfy:BaseUrl must be an absolute HTTPS URL.")
            .Validate(
                options => !options.Enabled || IsValidTopic(options.Topic),
                "Ntfy:Topic must be a non-empty single path segment of at most 64 characters.")
            .Validate(
                options => !options.Enabled || !SecretPlaceholder.IsPlaceholder(options.AuthToken),
                "Ntfy:AuthToken must be configured with a non-placeholder value.")
            .Validate(
                options => options.PollSeconds is >= 5 and <= 86_400,
                "Ntfy:PollSeconds must be between 5 and 86400.")
            .Validate(
                options => options.StartupDelaySeconds >= 0,
                "Ntfy:StartupDelaySeconds must not be negative.")
            .Validate(
                options => options.RequestTimeoutSeconds is > 0 and <= 60,
                "Ntfy:RequestTimeoutSeconds must be between 1 and 60.")
            .Validate(
                options => options.MaxSnapshotAgeHours is > 0 and <= 720,
                "Ntfy:MaxSnapshotAgeHours must be between 1 and 720.")
            .Validate(
                options => options.MinViableDiscountPercent is >= 0 and <= 95,
                "Ntfy:MinViableDiscountPercent must be between 0 and 95.")
            .Validate(
                options => options.DispatchBatchSize is > 0 and <= 200,
                "Ntfy:DispatchBatchSize must be between 1 and 200.")
            .Validate(
                options => options.MaxAttempts is >= 1 and <= 20,
                "Ntfy:MaxAttempts must be between 1 and 20.")
            .Validate(
                options => options.RetryBaseSeconds > 0 && options.RetryMaxSeconds >= options.RetryBaseSeconds,
                "Ntfy:RetryMaxSeconds must be >= RetryBaseSeconds > 0.")
            .Validate(
                options => options.MinSecondsBetweenSends >= 0,
                "Ntfy:MinSecondsBetweenSends must not be negative.")
            .ValidateOnStart();

        // The dispatcher's retry deadline needs a clock; the system one unless the host already supplied one.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IDealAlertOutbox, EfDealAlertOutbox>();

        // One scoped reader behind both read-only interfaces, so a cycle shares a single snapshot view.
        services.AddScoped<DealAlertCandidateReader>();
        services.AddScoped<IDealAlertCandidateReader>(provider => provider.GetRequiredService<DealAlertCandidateReader>());
        services.AddScoped<IDealAlertCandidateSource>(provider => provider.GetRequiredService<DealAlertCandidateReader>());

        services.AddScoped<DealAlertProducer>();
        services.AddScoped<DealAlertDispatcher>();

        services.AddHttpClient<INtfyAlertSender, NtfyAlertSender>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<NtfyOptions>>().Value;
            if (!IsHttps(options.BaseUrl))
            {
                throw new InvalidOperationException("Ntfy:BaseUrl must be an absolute HTTPS URL.");
            }

            client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 60));
        });

        services.AddHostedService<NtfyDealsJob>();

        return services;
    }

    private static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsValidTopic(string? topic) =>
        !string.IsNullOrWhiteSpace(topic) &&
        topic.Length <= 64 &&
        topic.IndexOf('/') < 0;

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";
}
