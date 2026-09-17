using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Itad;

namespace Deals.BusinessLogic.Services;

public sealed record ItadClientSettings(string ApiKey, string Country, IReadOnlySet<string> OfficialShopIds);

/// <summary>
/// Global in-process request budget for IsThereAnyDeal: 1 request/second sustained, burst of 10,
/// no queueing. Registered as a singleton so every caller shares one bucket.
/// </summary>
public sealed class ItadRequestGovernor : IDisposable
{
    private readonly TokenBucketRateLimiter limiter = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 10,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        AutoReplenishment = true,
        QueueLimit = 0
    });

    /// <summary>
    /// Takes a permit without queueing. Throws <see cref="HttpRequestException"/> when the bucket is empty.
    /// </summary>
    public async ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
    {
        var lease = await limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            throw new HttpRequestException("IsThereAnyDeal request budget is exhausted.");
        }

        return lease;
    }

    public void Dispose() => limiter.Dispose();
}

public sealed class ItadClient(HttpClient httpClient, ItadClientSettings settings, ItadRequestGovernor governor)
    : IItadClient
{
    private const string ApiKeyHeader = "ITAD-API-Key";
    private const string LookupPath = "lookup/id/shop/61/v1";
    private const string PricesPath = "games/prices/v3";
    private const int MaxIdsPerRequest = 200;

    // Name lists (DRM/platforms) are bounded to match the frontend contract: at most 20 entries of
    // at most 80 characters each, so a hostile payload cannot inflate the response or the render.
    private const int MaxNamesPerList = 20;
    private const int MaxNameLength = 80;

    public async Task<string?> LookupSteamAppIdAsync(int appId, CancellationToken cancellationToken)
    {
        if (appId <= 0)
        {
            return null;
        }

        var lookupKey = $"app/{appId}";
        var payload = JsonSerializer.Serialize(new[] { lookupKey });
        using var response = await PostWithRetryAsync(LookupPath, payload, withApiKey: false, cancellationToken);
        EnsureSuccess(response, "lookup");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(lookupKey, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var itadId = value.GetString()?.Trim();
        return itadId != null && Guid.TryParse(itadId, out _) ? itadId : null;
    }

    public async Task<IReadOnlyList<ItadGamePrices>> GetPricesAsync(
        IReadOnlyCollection<string> itadIds,
        CancellationToken cancellationToken)
    {
        var ids = itadIds
            .Where(id => Guid.TryParse(id, out _))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var prices = new List<ItadGamePrices>(ids.Count);
        foreach (var batch in ids.Chunk(MaxIdsPerRequest))
        {
            prices.AddRange(await GetPricesBatchAsync(batch, cancellationToken));
        }

        return prices;
    }

    private async Task<IReadOnlyList<ItadGamePrices>> GetPricesBatchAsync(
        string[] itadIds,
        CancellationToken cancellationToken)
    {
        var shopIds = string.Join(',', settings.OfficialShopIds);
        var path = $"{PricesPath}?country={Uri.EscapeDataString(settings.Country)}&shops={Uri.EscapeDataString(shopIds)}";
        var payload = JsonSerializer.Serialize(itadIds);
        using var response = await PostWithRetryAsync(path, payload, withApiKey: true, cancellationToken);
        EnsureSuccess(response, "prices");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement.EnumerateArray()
            .Select(ToGamePrices)
            .Where(game => game != null)
            .Select(game => game!)
            .ToList();
    }

    private async Task<HttpResponseMessage> PostWithRetryAsync(
        string path,
        string payload,
        bool withApiKey,
        CancellationToken cancellationToken)
    {
        var response = await PostAsync(path, payload, withApiKey, cancellationToken);
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return response;
        }

        var retryAfter = GetRetryAfter(response) ?? TimeSpan.FromSeconds(1);
        response.Dispose();

        await Task.Delay(retryAfter, cancellationToken);
        return await PostAsync(path, payload, withApiKey, cancellationToken);
    }

    private async Task<HttpResponseMessage> PostAsync(
        string path,
        string payload,
        bool withApiKey,
        CancellationToken cancellationToken)
    {
        using var lease = await governor.AcquireAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        if (withApiKey)
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, settings.ApiKey);
        }

        // ResponseContentRead (default) buffers the body, so disposing the request and its
        // content before the caller reads the response is safe.
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private ItadGamePrices? ToGamePrices(JsonElement item)
    {
        var itadId = GetString(item, "id")?.Trim();
        if (string.IsNullOrWhiteSpace(itadId) || !Guid.TryParse(itadId, out _))
        {
            return null;
        }

        var deals = new List<ItadDeal>();
        if (item.TryGetProperty("deals", out var dealsElement) && dealsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var dealElement in dealsElement.EnumerateArray())
            {
                var deal = ToDeal(dealElement);
                if (deal != null)
                {
                    deals.Add(deal);
                }
            }
        }

        return new ItadGamePrices(itadId, ToHistoryLow(item), deals);
    }

    private ItadDeal? ToDeal(JsonElement item)
    {
        var shopId = GetIdString(item, "shop", "id");
        var shopName = GetString(item, "shop", "name");
        var currency = GetString(item, "price", "currency");
        var currentPriceMinor = GetInt32(item, "price", "amountInt");

        if (string.IsNullOrWhiteSpace(shopId) ||
            string.IsNullOrWhiteSpace(shopName) ||
            string.IsNullOrWhiteSpace(currency) ||
            currentPriceMinor is null)
        {
            return null;
        }

        return new ItadDeal(
            shopId,
            shopName,
            settings.OfficialShopIds.Contains(shopId),
            currency,
            GetInt32(item, "regular", "amountInt"),
            currentPriceMinor,
            GetInt32(item, "cut"),
            GetString(item, "url"),
            GetTimestamp(item) ?? DateTime.UtcNow,
            GetNames(item, "drm"),
            GetNames(item, "platforms"));
    }

    /// <summary>
    /// Reads a provider array of <c>{ id, name }</c> objects into trimmed, case-insensitively distinct
    /// non-empty names, bounded to <see cref="MaxNamesPerList"/> entries of at most
    /// <see cref="MaxNameLength"/> characters so an abusive payload cannot inflate the response.
    /// Absent, malformed, or nameless entries contribute nothing: only the human-readable name is
    /// kept, provider ids are never parsed or hardcoded.
    /// </summary>
    private static IReadOnlyList<string> GetNames(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in array.EnumerateArray())
        {
            var name = entry.ValueKind == JsonValueKind.Object ? GetString(entry, "name")?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var bounded = name.Length > MaxNameLength ? name[..MaxNameLength] : name;
            if (!seen.Add(bounded))
            {
                continue;
            }

            names.Add(bounded);
            if (names.Count == MaxNamesPerList)
            {
                break;
            }
        }

        return names;
    }

    private static ItadHistoryLow? ToHistoryLow(JsonElement item)
    {
        if (!item.TryGetProperty("historyLow", out var historyLow) || historyLow.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ItadHistoryLow(
            ToAmount(historyLow, "all"),
            ToAmount(historyLow, "y1"),
            ToAmount(historyLow, "m3"));
    }

    private static ItadAmount? ToAmount(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var amountMinor = GetInt32(value, "amountInt");
        var currency = GetString(value, "currency");
        return amountMinor is null && string.IsNullOrWhiteSpace(currency)
            ? null
            : new ItadAmount(amountMinor, currency);
    }

    private static DateTime? GetTimestamp(JsonElement element)
    {
        var value = GetString(element, "timestamp");
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"IsThereAnyDeal {operation} is unavailable.",
                null,
                response.StatusCode);
        }
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        return retryAfter?.Date is { } date && date - DateTimeOffset.UtcNow is { Ticks: > 0 } wait
            ? wait
            : null;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    private static int? GetInt32(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        return value?.TryGetInt32(out var number) == true ? number : null;
    }

    private static string? GetIdString(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        if (value is not { } found)
        {
            return null;
        }

        if (found.ValueKind == JsonValueKind.String)
        {
            return found.GetString()?.Trim();
        }

        return found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static JsonElement? GetElement(JsonElement element, IEnumerable<string> path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return null;
            }
        }

        return element;
    }
}
