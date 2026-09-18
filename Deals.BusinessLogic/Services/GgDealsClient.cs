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
    private const int MaxUrlLength = 1024;

    // Fixed, credential-free messages: an exception message must never carry the request URL (which
    // holds the API key in its query string), the key itself, or any part of the payload.
    private const string MalformedPayloadMessage = "GG.deals returned a malformed response.";
    private const string UnsuccessfulPayloadMessage = "GG.deals did not report a successful response.";

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
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True)
        {
            throw new JsonException(UnsuccessfulPayloadMessage);
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        var prices = new Dictionary<int, GgDealsGamePrice>(ids.Count);
        foreach (var appId in ids)
        {
            // Only the ids this call asked for are inspected; anything else the provider echoes back
            // is ignored, so an unrelated malformed entry cannot fail an otherwise good response.
            if (!data.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var entry))
            {
                continue;
            }

            // An explicit JSON null is the provider's authoritative "not tracked / no offers".
            if (entry.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            // Any other non-object value is an unexpected shape: it must not be mistaken for
            // "no offers", or the caller would purge the persisted snapshot.
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(MalformedPayloadMessage);
            }

            prices[appId] = ToGamePrice(appId, entry);
        }

        return prices;
    }

    private static GgDealsGamePrice ToGamePrice(int appId, JsonElement game)
    {
        var title = ReadRequiredString(game, "title");
        var url = ReadRequiredString(game, "url");
        if (url.Length > MaxUrlLength ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        if (!game.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        var currency = ReadRequiredString(prices, "currency").ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        // currentKeyshops and historicalKeyshops are PLURAL in the API. Do not "correct" them to the
        // singular form: the request still succeeds and those fields come back null in silence.
        return new GgDealsGamePrice(
            appId,
            title,
            url,
            ReadPriceMinor(prices, "currentRetail"),
            ReadPriceMinor(prices, "currentKeyshops"),
            ReadPriceMinor(prices, "historicalRetail"),
            ReadPriceMinor(prices, "historicalKeyshops"),
            currency);
    }

    private static string ReadRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        return text;
    }

    /// <summary>
    /// Parses one optional price field into minor units. Absent properties and JSON null are the
    /// documented "no price" and stay <see langword="null"/>. A present field that is not a valid,
    /// finite, non-negative amount with room for the minor-unit conversion throws instead of turning
    /// into a silent <see langword="null"/>, because a silently dropped price is indistinguishable
    /// from an authoritative "no offers" and would purge the snapshot.
    /// </summary>
    private static int? ReadPriceMinor(JsonElement prices, string property)
    {
        if (!prices.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        decimal amount;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDecimal(out amount))
            {
                throw new JsonException(MalformedPayloadMessage);
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            // The provider sends JSON null for a missing price, but the literal "null" has also been
            // observed. Kept as a defensive "no price": it can only make a price disappear, never
            // fabricate one, and dropping it would fail an otherwise usable payload.
            var text = value.GetString()?.Trim();
            if (string.Equals(text, "null", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrEmpty(text) ||
                !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            {
                throw new JsonException(MalformedPayloadMessage);
            }
        }
        else
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        // System.Decimal cannot hold NaN or infinity, so a successfully parsed value is always finite
        // and only the sign and the int range below need guarding.
        if (amount < 0m)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        // Result stays decimal until the range check, so an outsized payload cannot overflow int.
        var minor = decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
        if (minor > int.MaxValue)
        {
            throw new JsonException(MalformedPayloadMessage);
        }

        return (int)minor;
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
}
