#nullable enable
using System.Net;
using Deals.API;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Models.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Checks the ntfy HTTP sender through a fake handler. No real socket, no real ntfy server: only the
/// request shape and the status-to-outcome mapping are asserted.
/// </summary>
internal static class NtfySenderChecks
{
    private const string BaseUrl = "https://ntfy.amitzi.xyz/";

    public static async Task RunAsync()
    {
        await DeliveredAndRequestShapeAsync();
        await StatusMappingAsync();
        await AmbiguousNetworkAsync();
        await CancellationPropagatesAsync();
        await OptionalHeadersAsync();
        Console.WriteLine("PASS: ntfy sender checks");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    private static (NtfyAlertSender Sender, THandler Handler, HttpClient Client) Build<THandler>(
        THandler handler,
        string token = "tok",
        string topic = "Deals")
        where THandler : HttpMessageHandler
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var options = Options.Create(new NtfyOptions { BaseUrl = BaseUrl, Topic = topic, AuthToken = token });
        return (new NtfyAlertSender(client, options, NullLogger<NtfyAlertSender>.Instance), handler, client);
    }

    private static async Task DeliveredAndRequestShapeAsync()
    {
        var handler = new RecordingHandler(_ => Responses.Status(HttpStatusCode.OK));
        var (sender, _, client) = Build(handler);
        using var _client = client;

        var message = new DealAlertMessage("Título", "Cuerpo", "https://store.steampowered.com/app/1/");
        var outcome = await sender.SendAsync(message, CancellationToken.None);

        Check(outcome == NtfySendOutcome.Delivered, "200 is delivered");
        Check(handler.Method == HttpMethod.Post, "POST method");
        Check(handler.RequestUri == new Uri($"{BaseUrl}Deals"), "request goes to base URL + topic");
        Check(handler.AuthorizationScheme == "Bearer", "bearer scheme");
        Check(handler.AuthorizationParameter == "tok", "bearer token from options");
        Check(handler.Title == "Título", "Title header carries the alert title");
        Check(handler.Body == "Cuerpo", "body is the alert body");
        Check(handler.Click == "https://store.steampowered.com/app/1/", "Click header carries the offer URL");
    }

    private static async Task StatusMappingAsync()
    {
        var cases = new (HttpStatusCode Code, NtfySendOutcome Expected, string Name)[]
        {
            (HttpStatusCode.NoContent, NtfySendOutcome.Delivered, "204 delivered"),
            (HttpStatusCode.BadRequest, NtfySendOutcome.Permanent, "400 permanent"),
            (HttpStatusCode.Unauthorized, NtfySendOutcome.Permanent, "401 permanent"),
            (HttpStatusCode.Forbidden, NtfySendOutcome.Permanent, "403 permanent"),
            (HttpStatusCode.NotFound, NtfySendOutcome.Permanent, "404 permanent"),
            (HttpStatusCode.RequestTimeout, NtfySendOutcome.Retryable, "408 retryable"),
            (HttpStatusCode.TooManyRequests, NtfySendOutcome.Retryable, "429 retryable"),
            (HttpStatusCode.InternalServerError, NtfySendOutcome.Retryable, "500 retryable"),
            (HttpStatusCode.ServiceUnavailable, NtfySendOutcome.Retryable, "503 retryable"),
        };

        foreach (var item in cases)
        {
            var handler = new RecordingHandler(_ => Responses.Status(item.Code));
            var (sender, _, client) = Build(handler);
            using var _client = client;
            var outcome = await sender.SendAsync(new DealAlertMessage("t", "b", null), CancellationToken.None);
            Check(outcome == item.Expected, item.Name);
        }
    }

    private static async Task AmbiguousNetworkAsync()
    {
        var timeout = new ThrowingHandler(_ => new TaskCanceledException("timeout", new TimeoutException()));
        var (timeoutSender, _, timeoutClient) = Build(timeout);
        using (timeoutClient)
        {
            var outcome = await timeoutSender.SendAsync(new DealAlertMessage("t", "b", null), CancellationToken.None);
            Check(outcome == NtfySendOutcome.Ambiguous, "timeout is ambiguous (at-least-once)");
        }

        var connection = new ThrowingHandler(_ => new HttpRequestException("connection refused"));
        var (connectionSender, _, connectionClient) = Build(connection);
        using (connectionClient)
        {
            var outcome = await connectionSender.SendAsync(new DealAlertMessage("t", "b", null), CancellationToken.None);
            Check(outcome == NtfySendOutcome.Ambiguous, "connection failure is ambiguous");
        }
    }

    private static async Task CancellationPropagatesAsync()
    {
        var handler = new ThrowingHandler(_ => new TaskCanceledException("shutdown"));
        var (sender, _, client) = Build(handler);
        using var _client = client;

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await sender.SendAsync(new DealAlertMessage("t", "b", null), cancellation.Token);
            throw new Exception("expected cancellation to propagate");
        }
        catch (OperationCanceledException)
        {
            // Shutdown must bubble, not be swallowed as an ambiguous delivery.
        }
    }

    private static async Task OptionalHeadersAsync()
    {
        // A non-HTTPS click target is dropped; an empty token sends no Authorization header.
        var handler = new RecordingHandler(_ => Responses.Status(HttpStatusCode.OK));
        var (sender, _, client) = Build(handler, token: string.Empty);
        using var _client = client;

        var outcome = await sender.SendAsync(
            new DealAlertMessage("t", "b", "http://insecure.example/x"),
            CancellationToken.None);

        Check(outcome == NtfySendOutcome.Delivered, "no-token delivery still succeeds");
        Check(handler.AuthorizationScheme is null, "no token means no Authorization header");
        Check(handler.Click is null, "non-HTTPS click target is dropped");
    }
}
