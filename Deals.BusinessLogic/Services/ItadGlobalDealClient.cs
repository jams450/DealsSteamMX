using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Global ITAD deals client (<c>deals/v2</c>). It reuses the existing <see cref="ItadClientSettings"/>
/// (key, country, official shop allowlist) and the same <see cref="ItadRequestGovernor"/> singleton the
/// per-game client uses, so the whole ITAD surface shares one local budget.
///
/// Contract facts used here come from the primary published contract, IsThereAnyDeal API 2.11.0
/// OpenAPI (<c>https://docs.isthereanydeal.com/openapi.yaml</c>), <c>GET /deals/v2</c> -> <c>resp.deals</c>
/// and its published <c>resp.deals.get.v2.success</c> example: object with required <c>nextOffset</c>,
/// <c>hasMore</c> and <c>list</c>; each item carries the game (<c>id</c>/<c>slug</c>/<c>title</c>/
/// <c>type</c>/<c>mature</c>) and a required nested <c>deal</c> object. No live key call was made; the
/// parser is strict about the fields it must store and never fabricates one it does not see.
/// </summary>
public sealed class ItadGlobalDealClient(
    HttpClient httpClient,
    ItadClientSettings settings,
    ItadRequestGovernor governor) : IGlobalDealClient
{
    private const string ApiKeyHeader = "ITAD-API-Key";
    private const string DealsPath = "deals/v2";

    /// <summary>Documented sort token: highest discount first. The single place to change the order.</summary>
    public const string SortToken = "-cut";

    /// <summary>Provider cap for one page; a larger limit is clamped, never sent.</summary>
    public const int MaxPageSize = ItadGlobalDealLimits.MaxPageSize;

    // Persistence/display bounds. Identity fields are rejected when oversized; free text is sanitized.
    private const int MaxDealKeyLength = 128;
    private const int MaxGameIdLength = 36;
    private const int MaxGameTitleLength = 512;
    private const int MaxGameSlugLength = 256;
    private const int MaxGameTypeLength = 32;
    private const int MaxShopIdLength = 32;
    private const int MaxShopNameLength = 128;
    private const int MaxUrlLength = 1024;

    // Fixed, credential-free message. An exception must never echo the request URL, the API key or any
    // part of the payload it failed to parse.
    private const string MalformedDealsMessage = "IsThereAnyDeal deals returned a malformed response.";
    private const string UnavailableDealsMessage = "IsThereAnyDeal deals is unavailable.";

    public async Task<GlobalDealPage> GetDealsPageAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var boundedLimit = Math.Clamp(limit, 1, MaxPageSize);
        var shopIds = string.Join(',', settings.OfficialShopIds);
        var path = $"{DealsPath}?country={Uri.EscapeDataString(settings.Country)}" +
            $"&shops={Uri.EscapeDataString(shopIds)}" +
            $"&sort={Uri.EscapeDataString(SortToken)}" +
            $"&offset={offset}&limit={boundedLimit}";
        using var response = await GetWithRetryAsync(path, priority, cancellationToken);
        EnsureSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadPage(document.RootElement, offset);
    }

    /// <summary>
    /// Reads the page envelope. The root must be an object with a <c>list</c> array and a boolean
    /// <c>hasMore</c>; anything else throws so a partial page can never be read as an authoritative
    /// empty catalog. <c>nextOffset</c> is preferred when it is a valid non-negative integer, otherwise
    /// the client computes <c>offset + observed items</c>; either way the sweep owns the loop guard.
    /// </summary>
    private GlobalDealPage ReadPage(JsonElement root, int offset)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        var rawCount = list.GetArrayLength();
        var deals = new List<ItadGlobalDeal>(rawCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            var deal = ToDeal(item);
            if (deal is not null && seen.Add(deal.DealKey))
            {
                deals.Add(deal);
            }
        }

        if (!root.TryGetProperty("hasMore", out var hasMore) ||
            hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            // Without the provider's own flag a short page cannot be told from a truncated one.
            throw new JsonException(MalformedDealsMessage);
        }

        var more = hasMore.GetBoolean();
        if (!more)
        {
            return new GlobalDealPage(deals, false, null);
        }

        var nextOffset = root.TryGetProperty("nextOffset", out var next) &&
            next.ValueKind == JsonValueKind.Number &&
            next.TryGetInt32(out var parsed) &&
            parsed >= 0
                ? parsed
                : offset + rawCount;

        return new GlobalDealPage(deals, true, nextOffset);
    }

    /// <summary>
    /// Maps one entry of <c>list</c>, returning null only for a genuinely expired deal (no longer
    /// active). A structurally broken deal throws: dropping it would let a partial page purge the
    /// previous snapshot.
    ///
    /// The published shape (IsThereAnyDeal API 2.11.0 OpenAPI, <c>resp.deals</c> and its
    /// <c>resp.deals.get.v2.success</c> example) carries the game at the list-item level
    /// (<c>id</c>/<c>slug</c>/<c>title</c>/<c>type</c>/<c>mature</c>) and the offer in a nested
    /// <c>deal</c> object. The contract publishes no deal id, so the snapshot key is composed from the
    /// game id and the shop id.
    /// </summary>
    private ItadGlobalDeal? ToDeal(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        // `id` is the ITAD game UUID (the contract publishes no separate deal id).
        var itadGameId = GetIdString(item, "id")?.Trim();
        if (string.IsNullOrEmpty(itadGameId) || itadGameId.Length > MaxGameIdLength || !Guid.TryParse(itadGameId, out _))
        {
            throw new JsonException(MalformedDealsMessage);
        }

        var gameTitle = SanitizeText(ReadString(item, "title"), MaxGameTitleLength);
        if (string.IsNullOrEmpty(gameTitle))
        {
            throw new JsonException(MalformedDealsMessage);
        }

        // `deal` is required and holds the whole offer (shop, price, cut, historyLow, expiry, url).
        if (GetElement(item, "deal") is not { ValueKind: JsonValueKind.Object } deal)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        var shopId = GetIdString(deal, "shop", "id")?.Trim();
        if (string.IsNullOrEmpty(shopId) || shopId.Length > MaxShopIdLength)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        var shopName = SanitizeText(ReadString(deal, "shop", "name"), MaxShopNameLength);
        if (string.IsNullOrEmpty(shopName))
        {
            throw new JsonException(MalformedDealsMessage);
        }

        var dealKey = $"{itadGameId}:{shopId}";
        if (dealKey.Length > MaxDealKeyLength)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        // The price and its currency are the core of the record: a malformed pair fails the page rather
        // than being stored as a fake zero or an empty currency.
        var currency = ReadCurrency(deal);
        var currentPriceMinor = ReadRequiredAmount(deal, "price", "amountInt");
        var regularPriceMinor = ReadOptionalAmount(deal, "regular", "amountInt");
        var discountPercent = GlobalDealPolicy.DiscountPercent(GetInt32(deal, "cut"), currentPriceMinor, regularPriceMinor);

        var (historyLowMinor, historyLowCurrency) = ReadHistoryLow(deal);

        // An expired deal is not an active deal; the catalog read also guards this defensively.
        var expiry = ReadTimestamp(deal, "expiry");
        if (expiry is { } expiresAt && expiresAt <= DateTime.UtcNow)
        {
            return null;
        }

        return new ItadGlobalDeal(
            dealKey,
            itadGameId,
            gameTitle,
            SanitizeText(TryReadString(item, "slug"), MaxGameSlugLength),
            SanitizeText(TryReadString(item, "type"), MaxGameTypeLength),
            TryReadBool(item, "mature") ?? false,
            shopId,
            shopName,
            settings.OfficialShopIds.Contains(shopId),
            currency,
            currentPriceMinor,
            regularPriceMinor,
            discountPercent,
            historyLowMinor,
            historyLowCurrency,
            TryReadOptionalHttpsUrl(TryReadString(deal, "url")),
            expiry);
    }

    /// <summary>
    /// Provider <c>historyLow</c>: a price object (<c>{amount, amountInt, currency}</c>) or null per the
    /// published contract. It is read only when present and is never synthesized from our own
    /// observations.
    /// </summary>
    private static (int? Minor, string? Currency) ReadHistoryLow(JsonElement deal)
    {
        if (GetElement(deal, "historyLow") is not { ValueKind: JsonValueKind.Object } low)
        {
            return (null, null);
        }

        var minor = TryReadAmount(low, "amountInt");
        var currency = TryReadCurrency(low, "currency");
        return (minor, currency);
    }

    private static string ReadCurrency(JsonElement deal)
    {
        var currency = TryReadString(deal, "price", "currency")?.Trim().ToUpperInvariant();
        if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new JsonException(MalformedDealsMessage);
        }

        return currency;
    }

    private static string? TryReadCurrency(JsonElement element, string property)
    {
        var currency = TryReadString(element, property)?.Trim().ToUpperInvariant();
        return currency is { Length: 3 } && currency.All(char.IsAsciiLetter) ? currency : null;
    }

    private static int ReadRequiredAmount(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        if (value is not { ValueKind: JsonValueKind.Number } found ||
            !found.TryGetInt32(out var amount) ||
            amount < 0)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        return amount;
    }

    private static int? ReadOptionalAmount(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        if (value is not { } found || found.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (found.ValueKind != JsonValueKind.Number ||
            !found.TryGetInt32(out var amount) ||
            amount < 0)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        return amount;
    }

    private static int? TryReadAmount(JsonElement element, string property)
    {
        var value = GetElement(element, property);
        return value is { ValueKind: JsonValueKind.Number } found &&
            found.TryGetInt32(out var amount) &&
            amount >= 0
                ? amount
                : null;
    }

    /// <summary>
    /// Reads an optional UTC timestamp. Absent or JSON null means "unknown". A present but unreadable
    /// value throws: the difference between an expired and an active deal cannot be guessed.
    /// </summary>
    private static DateTime? ReadTimestamp(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        var value = element.GetString()?.Trim();
        if (string.IsNullOrEmpty(value) ||
            !DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new JsonException(MalformedDealsMessage);
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    /// <summary>
    /// Reads a required string at <paramref name="path"/>. Absent, JSON null or a non-string value
    /// throws, because a missing identity field must not be read as an empty one.
    /// </summary>
    private static string? ReadString(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        if (value is not { } found || found.ValueKind == JsonValueKind.Null)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        if (found.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(MalformedDealsMessage);
        }

        return found.GetString();
    }

    private static string? TryReadString(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        return value is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;
    }

    private static bool? TryReadBool(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? TryReadOptionalHttpsUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxUrlLength)
        {
            return null;
        }

        // Verbatim on purpose: the ITAD affiliate tag must survive the round trip.
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? value
                : null;
    }

    /// <summary>
    /// Sanitizes free text shown to the user: control characters are stripped, then the result is
    /// trimmed and truncated without splitting a surrogate pair.
    /// </summary>
    private static string? SanitizeText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }

            if (builder.Length == maxLength)
            {
                break;
            }
        }

        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? null : sanitized;
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

    private static JsonElement? GetElement(JsonElement element, params string[] path)
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

    private async Task<HttpResponseMessage> GetWithRetryAsync(
        string path,
        ItadRequestPriority priority,
        CancellationToken cancellationToken)
    {
        var response = await GetAsync(path, priority, cancellationToken);
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return response;
        }

        var retryAfter = GetRetryAfter(response) ?? TimeSpan.FromSeconds(1);
        response.Dispose();

        await Task.Delay(retryAfter, cancellationToken);
        return await GetAsync(path, priority, cancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(
        string path,
        ItadRequestPriority priority,
        CancellationToken cancellationToken)
    {
        using var lease = await governor.AcquireAsync(priority, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, settings.ApiKey);

        // ResponseContentRead buffers the page, so the request can be disposed before the caller reads it.
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(UnavailableDealsMessage, null, response.StatusCode);
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
}
