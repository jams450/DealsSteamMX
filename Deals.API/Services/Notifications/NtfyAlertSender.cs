using System.Net.Http.Headers;
using System.Text;
using Deals.API;
using Deals.BusinessLogic.Models.Notifications;
using Microsoft.Extensions.Options;

namespace Deals.API.Services.Notifications;

/// <summary>Result of one delivery attempt, mapped from the HTTP status (or the absence of one).</summary>
public enum NtfySendOutcome
{
    /// <summary>2xx. The only outcome that lets the outbox mark the row sent.</summary>
    Delivered,

    /// <summary>408, 429 or 5xx: retry with backoff.</summary>
    Retryable,

    /// <summary>Any other non-2xx (401/403/404/413...): the message will never be accepted as-is.</summary>
    Permanent,

    /// <summary>
    /// The HTTP call timed out or the connection dropped before a status arrived. Delivery is unknown:
    /// the row is retried, which makes the channel at-least-once, never exactly-once. That trade is
    /// deliberate — a lost deal is worse than a repeated one, and the outbox dedup key still prevents a
    /// repeat of the same price from being re-queued.
    /// </summary>
    Ambiguous,
}

/// <summary>Abstraction so the dispatcher can be checked with a fake, without a network.</summary>
public interface INtfyAlertSender
{
    Task<NtfySendOutcome> SendAsync(DealAlertMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Single ntfy client. Posts one plain-text message to the configured topic with a Bearer token.
///
/// Safety rules that are part of the contract:
/// - only the HTTP status decides the outcome; the response body is never read or logged;
/// - the auth token, the full URL and the topic never reach a log or an exception message;
/// - a timeout is reported as <see cref="NtfySendOutcome.Ambiguous"/>, not as a failure.
/// </summary>
public sealed class NtfyAlertSender(
    HttpClient http,
    IOptions<NtfyOptions> options,
    ILogger<NtfyAlertSender> logger) : INtfyAlertSender
{
    private readonly HttpClient _http = http;
    private readonly NtfyOptions _options = options.Value;
    private readonly ILogger<NtfyAlertSender> _logger = logger;

    public async Task<NtfySendOutcome> SendAsync(DealAlertMessage message, CancellationToken cancellationToken)
    {
        var topic = _options.Topic;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(topic, UriKind.Relative));
        request.Content = new StringContent(message.Body, Encoding.UTF8, "text/plain");
        request.Headers.TryAddWithoutValidation("Title", message.Title);
        if (!string.IsNullOrWhiteSpace(message.ClickUrl) && IsHttps(message.ClickUrl))
        {
            request.Headers.TryAddWithoutValidation("Click", message.ClickUrl);
        }

        if (!string.IsNullOrWhiteSpace(_options.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AuthToken);
        }

        try
        {
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return NtfySendOutcome.Delivered;
            }

            var status = (int)response.StatusCode;
            // Status only: never the body, never the URL.
            _logger.LogWarning("[ntfy] delivery rejected status={Status}", status);

            return status is 408 or 429 || status >= 500
                ? NtfySendOutcome.Retryable
                : NtfySendOutcome.Permanent;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout fired. No status arrived; the message may or may not have been delivered.
            _logger.LogWarning("[ntfy] delivery ambiguous after timeout");
            return NtfySendOutcome.Ambiguous;
        }
        catch (HttpRequestException)
        {
            // Connection-level failure: same ambiguity, retry on the next cycle.
            _logger.LogWarning("[ntfy] delivery ambiguous after connection failure");
            return NtfySendOutcome.Ambiguous;
        }
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
