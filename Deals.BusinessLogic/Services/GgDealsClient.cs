using System.Globalization;
using System.Net;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.GgDeals;

namespace Deals.BusinessLogic.Services;

public sealed record GgDealsClientSettings(string ApiKey, string Region);

public sealed class GgDealsClient(HttpClient httpClient, GgDealsClientSettings settings, ProviderRequestGovernor governor)
    : IGgDealsClient
{
    private const string PricesPath = "prices/by-steam-app-id/";

    // The provider counts every id as one record against its 100/minute budget and does not offer
    // batching beyond that, so an oversized call is refused instead of being split silently: the
    // caller has to know it went over the limit.
    private const int MaxIdsPerRequest = 100;

    private const int MaxRetryWaitSeconds = 5;
    private static readonly TimeSpan FallbackRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly IReadOnlyDictionary<int, GgDealsGamePrice> Empty =
        new Dictionary<int, GgDealsGamePrice>();

    public async Task<IReadOnlyDictionary<int, GgDealsGamePrice>> GetPricesAsync(
        IReadOnlyCollection<int> steamAppIds,
        CancellationToken ct)
    {
        var ids = steamAppIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return Empty;
        }

        if (ids.Count > MaxIdsPerRequest)
        {
            throw new ArgumentException(
                $"GG.deals accepts at most {MaxIdsPerRequest} ids per request.",
                nameof(steamAppIds));
        }

        using var response = await GetWithRetryAsync(ids, ct);
        EnsureSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var prices = new Dictionary<int, GgDealsGamePrice>();
        foreach (var entry in data.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) ||
                appId <= 0)
            {
                continue;
            }

            // A JSON null (or anything that is not an object) means the game is not tracked by gg.deals.
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var game = ToGamePrice(appId, entry.Value);
            if (game != null)
            {
                prices[appId] = game;
            }
        }

        return prices;
    }

    private static GgDealsGamePrice? ToGamePrice(int appId, JsonElement game)
    {
        var title = GetString(game, "title")?.Trim();
        var url = GetString(game, "url")?.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var prices = GetElement(game, "prices");
        var currency = prices is { ValueKind: JsonValueKind.Object } pricesObject
            ? GetString(pricesObject, "currency")?.Trim()
            : null;

        // currentKeyshops and historicalKeyshops are PLURAL in the API. Do not "correct" them to the
        // singular form: the request still succeeds and those fields come back null in silence.
        return new GgDealsGamePrice(
            appId,
            title,
            url,
            ToMinor(GetString(prices, "currentRetail")),
            ToMinor(GetString(prices, "currentKeyshops")),
            ToMinor(GetString(prices, "historicalRetail")),
            ToMinor(GetString(prices, "historicalKeyshops")),
            currency ?? string.Empty);
    }

    /// <summary>
    /// Parses the provider's two-decimal string ("91.99") into minor units. Missing values, JSON null,
    /// blank values, the literal "null" and unparseable values all become <see langword="null"/>; an
    /// out-of-range amount is dropped the same way instead of overflowing.
    /// </summary>
    private static int? ToMinor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "null", StringComparison.Ordinal))
        {
            return null;
        }

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        // Result stays decimal until the range check, so an outsized payload cannot overflow int.
        var minor = decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
        return minor < int.MinValue || minor > int.MaxValue ? null : (int)minor;
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(
        IReadOnlyList<int> appIds,
        CancellationToken ct)
    {
        var response = await GetAsync(appIds, ct);
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return response;
        }

        var retryDelay = GetRetryDelay(response);
        response.Dispose();

        await Task.Delay(retryDelay, ct);
        return await GetAsync(appIds, ct);
    }

    private async Task<HttpResponseMessage> GetAsync(IReadOnlyList<int> appIds, CancellationToken ct)
    {
        // The governor is shared with ITAD. When the shared bucket is empty it throws
        // HttpRequestException, which is not swallowed here: the caller must degrade this provider
        // on its own without taking the other one down.
        using var lease = await governor.AcquireAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildPath(appIds));

        // ResponseContentRead (default) buffers the body, so disposing the request and its
        // content before the caller reads the response is safe.
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    /// <summary>
    /// Builds the request path. The API key travels in the query string because that is the only
    /// supported location; the key is never logged and never echoed into an exception message.
    /// </summary>
    private string BuildPath(IReadOnlyList<int> appIds)
    {
        var ids = string.Join(',', appIds);
        var key = Uri.EscapeDataString(settings.ApiKey);
        var region = Uri.EscapeDataString(settings.Region);
        return $"{PricesPath}?ids={ids}&key={key}&region={region}";
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "GG.deals prices are unavailable.",
                null,
                response.StatusCode);
        }
    }

    /// <summary>
    /// Reads the documented <c>x-ratelimit-reset</c> header. This API does not document
    /// <c>Retry-After</c>. Anything that is not a sane positive number of seconds falls back to a
    /// short fixed delay, and the wait is capped so a bad header cannot stall the request.
    /// </summary>
    private static TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryWaitSeconds));
        }

        return FallbackRetryDelay;
    }

    private static string? GetString(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } found &&
        found.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? GetElement(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value
            : null;
}
