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
/// Shared, provider-agnostic request budget for every price provider (ITAD, gg.deals, ...):
/// 1 request/second sustained, burst of 10, no queueing. Registered as a singleton so every caller
/// shares one bucket.
/// </summary>
public sealed class ProviderRequestGovernor : IDisposable
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
            throw new HttpRequestException("Shared price provider request budget is exhausted.");
        }

        return lease;
    }

    public void Dispose() => limiter.Dispose();
}

public sealed class ItadClient(HttpClient httpClient, ItadClientSettings settings, ProviderRequestGovernor governor)
    : IItadClient
{
    private const string ApiKeyHeader = "ITAD-API-Key";
    private const string LookupPath = "lookup/id/shop/61/v1";
    private const string PricesPath = "games/prices/v3";
    private const string BundlesPath = "games/overview/v2";
    private const int MaxIdsPerRequest = 200;

    // Name lists (DRM/platforms) are bounded to match the frontend contract: at most 20 entries of
    // at most 80 characters each, so a hostile payload cannot inflate the response or the render.
    private const int MaxNamesPerList = 20;
    private const int MaxNameLength = 80;

    // Offer fields that are stored and compared: bounds match the persistence contract, so an
    // oversized value is treated as a malformed payload instead of being silently truncated.
    private const int MaxShopIdLength = 32;
    private const int MaxShopNameLength = 128;
    private const int MaxUrlLength = 1024;

    // Bundle display fields. The id, title and shop bounds match the persistence contract; details and the
    // tier caps mirror the frontend contract so an abusive payload cannot inflate either the row or the
    // response. Free text is truncated, identity fields are rejected when oversized.
    private const int MaxBundleKeyLength = 128;
    private const int MaxBundleTitleLength = 512;
    private const int MaxBundleTypeLength = 32;
    private const int MaxBundleShopIdLength = 32;
    private const int MaxBundleShopNameLength = 128;
    private const int MaxBundleDetailsLength = 600;

    /// <summary>
    /// Hard caps for parsing. <see cref="MaxTiersPerBundle"/> matches the display contract; tier items are
    /// read further than they are persisted because they decide which game the bundle belongs to.
    /// </summary>
    private const int MaxTiersPerBundle = 20;
    private const int MaxTierItems = 200;

    // Fixed, credential-free messages: an exception must never echo the request URL, the API key, or
    // any part of the payload it failed to parse.
    private const string MalformedLookupMessage = "IsThereAnyDeal lookup returned a malformed response.";
    private const string MalformedPricesMessage = "IsThereAnyDeal prices returned a malformed response.";
    private const string MalformedBundlesMessage = "IsThereAnyDeal bundles returned a malformed response.";

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
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedLookupMessage);
        }

        // Absent or explicit null is the provider's authoritative "this Steam app is not tracked".
        if (!root.TryGetProperty(lookupKey, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // Present but not a string is an unexpected shape: it must not be read as "not tracked".
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(MalformedLookupMessage);
        }

        var itadId = value.GetString()?.Trim();
        if (itadId != null && Guid.TryParse(itadId, out _))
        {
            return itadId;
        }

        throw new JsonException(MalformedLookupMessage);
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

    /// <summary>
    /// Outbound bundle phase: POST games/overview/v2 with the queried ids. The response is one object with
    /// <c>prices[]</c> (ignored here) and a flat <c>bundles[]</c>, not an array of games: the caller
    /// attributes each bundle to the game it asked for through <c>tiers[].games[].id</c>. A malformed
    /// bundle is discarded instead of failing the batch, because bundles are auxiliary display metadata;
    /// a missing or non-array <c>bundles</c> does throw, because an empty list would look authoritative
    /// and purge the persisted snapshot.
    /// </summary>
    public async Task<IReadOnlyList<ItadBundle>> GetBundlesAsync(
        IReadOnlyCollection<string> itadIds,
        CancellationToken cancellationToken)
    {
        var ids = itadIds
            .Where(id => Guid.TryParse(id, out _))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var bundles = new Dictionary<string, ItadBundle>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ids.Chunk(MaxIdsPerRequest))
        {
            // One bundle can be reported for several queried ids: it is kept once, keyed by its own id.
            foreach (var bundle in await GetBundlesBatchAsync(batch, cancellationToken))
            {
                bundles.TryAdd(bundle.BundleKey, bundle);
            }
        }

        var result = bundles.Values.ToList();
        if (result.Any(bundle => !bundle.AttributionComplete))
        {
            // Membership could not be proven against the complete provider payload. Do not return an
            // apparently empty authoritative result: SteamGameService must preserve its snapshot.
            throw new JsonException(MalformedBundlesMessage);
        }

        return result;
    }

    private async Task<IReadOnlyList<ItadBundle>> GetBundlesBatchAsync(
        string[] itadIds,
        CancellationToken cancellationToken)
    {
        var path = $"{BundlesPath}?country={Uri.EscapeDataString(settings.Country)}";
        var payload = JsonSerializer.Serialize(itadIds);
        using var response = await PostWithRetryAsync(path, payload, withApiKey: true, cancellationToken);
        EnsureSuccess(response, "bundles");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadBundles(document.RootElement, DateTime.UtcNow, itadIds);
    }

    /// <summary>
    /// Reads flat <c>bundles[]</c>. Full tier/item membership is scanned before render caps are applied;
    /// attribution therefore cannot be affected by the JSON/UI limits.
    /// </summary>
    private static IReadOnlyList<ItadBundle> ReadBundles(JsonElement root, DateTime observedAt, IReadOnlyList<string> queriedIds)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("bundles", out var bundles) ||
            bundles.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedBundlesMessage);
        }

        var parsed = new List<ItadBundle>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in bundles.EnumerateArray())
        {
            var bundle = ToBundle(item, observedAt, queriedIds);
            if (bundle is null || !seen.Add(bundle.BundleKey))
            {
                continue;
            }

            parsed.Add(bundle);
        }

        return parsed;
    }

    /// <summary>
    /// Maps one entry of `bundles[]`, returning <see langword="null"/> for anything that cannot be shown
    /// honestly: a missing/oversized id or title, an unreadable or already-past expiry, or a missing or
    /// non-HTTPS `url`. Nothing extends or rewrites `url`: the affiliate tag must survive verbatim.
    /// </summary>
    private static ItadBundle? ToBundle(JsonElement item, DateTime observedAt, IReadOnlyList<string> queriedIds)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var bundleKey = GetIdString(item, "id");
        if (string.IsNullOrEmpty(bundleKey) || bundleKey.Length > MaxBundleKeyLength)
        {
            return null;
        }

        var title = SanitizeText(TryReadString(item, "title"), MaxBundleTitleLength);
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        // `url` is mandatory: the bundle is only useful with its (affiliate) link.
        var url = ReadHttpsUrl(item, "url", MaxUrlLength);
        if (url is null)
        {
            return null;
        }

        var (expiryReadable, expiresAt) = ReadTimestamp(item, "expiry");
        if (!expiryReadable)
        {
            // A present but unreadable date cannot prove the bundle is still active.
            return null;
        }

        if (expiresAt is { } expiry && expiry <= observedAt)
        {
            // An expired bundle is not an active bundle: it is dropped here and its relations are purged by
            // the refresh, so it is never shown as active.
            return null;
        }

        // `publish` is display metadata only: a value that cannot be parsed is left out, not fatal.
        var (_, publishedAt) = ReadTimestamp(item, "publish");

        var details = SanitizeText(TryReadString(item, "details"), MaxBundleDetailsLength);

        // `page` is documented as the shop descriptor and can be an object ({id,title}) or an HTTPS URL.
        // It is parsed flexibly: URL becomes page_url; object id/title becomes shop metadata. Other shapes
        // are ignored rather than guessed.
        var pageUrl = ReadHttpsUrl(item, "page", MaxUrlLength);
        var pageShopId = GetIdString(item, "page", "id");
        if (pageShopId is { Length: > MaxBundleShopIdLength })
        {
            pageShopId = null;
        }

        var pageShopName = SanitizeText(
            TryReadString(item, "page", "title") ?? TryReadString(item, "page", "name"),
            MaxBundleShopNameLength);

        var shopId = GetIdString(item, "shop", "id") ?? pageShopId;
        if (shopId is { Length: > MaxBundleShopIdLength })
        {
            shopId = null;
        }

        var shopName = SanitizeText(TryReadString(item, "shop", "name"), MaxBundleShopNameLength) ?? pageShopName;
        var (tiers, matchedItadIds, attributionComplete) = ReadTiers(item, queriedIds);

        return new ItadBundle(
            bundleKey,
            title,
            pageUrl,
            shopId,
            shopName,
            details,
            publishedAt,
            expiresAt,
            url,
            tiers,
            matchedItadIds,
            attributionComplete);
    }

    /// <summary>
    /// Scans every provider tier/item for attribution before applying render caps. The returned tiers are
    /// capped for persistence/UI, while MatchedItadIds and AttributionComplete reflect the full payload.
    /// All provider tiers and games are enumerated; caps apply only to the sanitized display projection.
    /// </summary>
    private static (IReadOnlyList<ItadBundleTier> Tiers, IReadOnlySet<string> MatchedItadIds, bool AttributionComplete)
        ReadTiers(JsonElement bundle, IReadOnlyList<string> queriedIds)
    {
        if (GetElement(bundle, "tiers") is not { ValueKind: JsonValueKind.Array } tiers)
        {
            return ([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);
        }

        var queried = queriedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<ItadBundleTier>(Math.Min(tiers.GetArrayLength(), MaxTiersPerBundle));
        var tierIndex = 0;
        var attributionComplete = true;

        foreach (var tier in tiers.EnumerateArray())
        {
            if (tier.ValueKind != JsonValueKind.Object)
            {
                attributionComplete = false;
                continue;
            }

            var allGames = GetElement(tier, "games");
            if (allGames is not { ValueKind: JsonValueKind.Array })
            {
                attributionComplete = false;
            }
            else
            {
                foreach (var game in allGames.Value.EnumerateArray())
                {
                    if (game.ValueKind == JsonValueKind.Object &&
                        GetIdString(game, "id") is { } id && queried.Contains(id))
                    {
                        matched.Add(id);
                    }
                }
            }

            if (tierIndex++ >= MaxTiersPerBundle)
            {
                continue;
            }

            var (priceMinor, currency) = ReadTierPrice(tier);
            var addon = IsAddon(tier);
            var (games, itemsComplete) = ReadTierGames(tier);
            if (priceMinor is null && !addon && games.Count == 0)
            {
                continue;
            }

            parsed.Add(new ItadBundleTier(priceMinor, currency, addon, itemsComplete, games));
        }

        // If provider content exceeded scan safety limit, no attribution is safe. Preserve snapshot.
        if (tiers.GetArrayLength() > MaxTiersPerBundle)
        {
            attributionComplete = false;
        }

        return (parsed, matched, attributionComplete);
    }

    private static (int? PriceMinor, string? Currency) ReadTierPrice(JsonElement tier)
    {
        if (GetElement(tier, "price") is not { ValueKind: JsonValueKind.Object } price)
        {
            // `price` is explicitly nullable in the contract: a tier without a price is valid.
            return (null, null);
        }

        var amount = GetInt32(price, "amountInt");
        var currency = TryReadString(price, "currency")?.Trim().ToUpperInvariant();

        // A lone amount or a lone currency cannot be rendered as a price, so the readable pair is required.
        if (amount is null or < 0 || currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            return (null, null);
        }

        return (amount, currency);
    }

    private static bool IsAddon(JsonElement tier) =>
        GetElement(tier, "addon") is { ValueKind: JsonValueKind.True };

    /// <summary>
    /// Reads the tier's item list. Items are read further than they are persisted because they decide which
    /// game the bundle belongs to, so a truncation is tracked instead of being implicit: the caller must
    /// know the tier is not complete for a comparison.
    /// </summary>
    private static (IReadOnlyList<ItadBundleItem> Items, bool Complete) ReadTierGames(JsonElement tier)
    {
        if (GetElement(tier, "games") is not { ValueKind: JsonValueKind.Array } games)
        {
            return ([], false);
        }

        var parsed = new List<ItadBundleItem>();
        var truncated = false;
        foreach (var game in games.EnumerateArray())
        {
            if (parsed.Count == MaxTierItems)
            {
                // More items than the safety cap: the tier cannot be proven complete.
                truncated = true;
                break;
            }

            if (game.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetIdString(game, "id");
            var title = SanitizeText(TryReadString(game, "title"), MaxBundleTitleLength);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title))
            {
                // Without both an id and a title the item can neither be attributed nor displayed.
                continue;
            }

            parsed.Add(new ItadBundleItem(
                id,
                title,
                SanitizeText(TryReadString(game, "type"), MaxBundleTypeLength),
                null,
                null));
        }

        return (parsed, !truncated);
    }

    /// <summary>
    /// Reads an optional timestamp. Absent means "unknown" and is not an error; a present but unreadable
    /// value is reported as unreadable so the caller decides whether the bundle must be discarded.
    /// </summary>
    private static (bool Readable, DateTime? Timestamp) ReadTimestamp(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return (true, null);
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return (false, null);
        }

        var value = element.GetString()?.Trim();
        if (string.IsNullOrEmpty(value) ||
            !DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return (false, null);
        }

        return (true, parsed);
    }

    private static string? ReadHttpsUrl(JsonElement item, string property, int maxLength) =>
        TryReadHttpsUrl(TryReadString(item, property), maxLength);

    /// <summary>
    /// Returns the value untouched when it is an absolute HTTPS URL within bounds, otherwise
    /// <see langword="null"/>. Never rewrites the URL: the affiliate tag must survive verbatim.
    /// </summary>
    private static string? TryReadHttpsUrl(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? value
                : null;
    }

    /// <summary>
    /// Sanitizes free text shown to the user: control characters are removed (a provider value must not
    /// smuggle control codes into the UI), then the result is trimmed and truncated. Truncation never
    /// splits a surrogate pair. Returns <see langword="null"/> for absent or empty input.
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

    /// <summary>
    /// Non-throwing optional string read used by the bundle mapper: bundles are discarded, not thrown
    /// for, so a non-string value collapses to <see langword="null"/> here.
    /// </summary>
    private static string? TryReadString(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        return value is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;
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
        var root = document.RootElement;

        // A non-array body is not "no prices": returning an empty list here would look like an
        // authoritative empty response and purge the persisted snapshot.
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        var games = new List<ItadGamePrices>(root.GetArrayLength());
        foreach (var item in root.EnumerateArray())
        {
            games.Add(ToGamePrices(item));
        }

        return games;
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

    private ItadGamePrices ToGamePrices(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        // The response is keyed by id; a game without a usable id cannot be matched back to a
        // request, so a malformed one fails the whole batch instead of being dropped.
        var itadId = ReadOptionalString(item, "id");
        if (string.IsNullOrEmpty(itadId) || !Guid.TryParse(itadId, out _))
        {
            throw new JsonException(MalformedPricesMessage);
        }

        return new ItadGamePrices(itadId, ToHistoryLow(item), ReadDeals(item));
    }

    /// <summary>
    /// Reads the deal array. Absent or JSON null means "no offers for this game" and is legitimate.
    /// A present non-array value, or a deal missing or malforming an offer-critical field (shop
    /// id/name, currency, current price), is rejected: silently skipping it could make the caller
    /// believe the game has no offers and discard its snapshot.
    /// </summary>
    private IReadOnlyList<ItadDeal> ReadDeals(JsonElement item)
    {
        if (!item.TryGetProperty("deals", out var dealsElement) || dealsElement.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (dealsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        var deals = new List<ItadDeal>();
        foreach (var dealElement in dealsElement.EnumerateArray())
        {
            deals.Add(ToDeal(dealElement));
        }

        return deals;
    }

    private ItadDeal ToDeal(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        var shopId = GetIdString(item, "shop", "id")?.Trim();
        if (string.IsNullOrEmpty(shopId) || shopId.Length > MaxShopIdLength)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        var shopName = ReadOptionalString(item, "shop", "name");
        if (string.IsNullOrEmpty(shopName) || shopName.Length > MaxShopNameLength)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        var currency = ReadCurrency(ReadOptionalString(item, "price", "currency"));
        var currentPriceMinor = ReadOptionalAmount(GetElement(item, "price", "amountInt"))
            ?? throw new JsonException(MalformedPricesMessage);

        return new ItadDeal(
            shopId,
            shopName,
            settings.OfficialShopIds.Contains(shopId),
            currency,
            ReadOptionalAmount(GetElement(item, "regular", "amountInt")),
            currentPriceMinor,
            GetInt32(item, "cut"),
            ReadOptionalHttpsUrl(item),
            GetTimestamp(item) ?? DateTime.UtcNow,
            GetNames(item, "drm"),
            GetNames(item, "platforms"));
    }

    /// <summary>
    /// Reads a three-letter currency code, normalized to uppercase. Missing, malformed, or
    /// non-ISO-shaped values are rejected rather than stored as-is.
    /// </summary>
    private static string ReadCurrency(string? value)
    {
        var currency = value?.ToUpperInvariant();
        if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetter))
        {
            throw new JsonException(MalformedPricesMessage);
        }

        return currency;
    }

    private static string? ReadOptionalCurrency(string? value) => value is null ? null : ReadCurrency(value);

    /// <summary>
    /// Reads an optional amount in minor units. Absent or JSON null means "not reported". A present
    /// value must be a non-negative 32-bit integer; anything else is malformed, not zero.
    /// </summary>
    private static int? ReadOptionalAmount(JsonElement? value)
    {
        if (value is not { } found || found.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (found.ValueKind != JsonValueKind.Number ||
            !found.TryGetInt32(out var number) ||
            number < 0)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        return number;
    }

    /// <summary>
    /// Reads an optional HTTPS URL, bounded to the persisted length. Absent, JSON null, or a blank
    /// string all mean "no URL"; a present non-string or non-HTTPS value is malformed.
    /// </summary>
    private static string? ReadOptionalHttpsUrl(JsonElement element)
    {
        var url = ReadOptionalString(element, "url");
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (url.Length > MaxUrlLength ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException(MalformedPricesMessage);
        }

        return url;
    }

    /// <summary>
    /// Reads a provider array of <c>{ id, name }</c> objects into trimmed, case-insensitively distinct
    /// non-empty names, bounded to <see cref="MaxNamesPerList"/> entries of at most
    /// <see cref="MaxNameLength"/> characters so an abusive payload cannot inflate the response.
    /// Absent or JSON null is "no metadata", and a missing/blank <c>name</c> is skipped because the
    /// provider sometimes omits the label; any other present shape (non-array, non-object entry, or a
    /// non-string <c>name</c>) is malformed and throws instead of being silently dropped.
    /// </summary>
    private static IReadOnlyList<string> GetNames(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(MalformedPricesMessage);
            }

            var name = ReadOptionalString(entry, "name");
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
        if (!item.TryGetProperty("historyLow", out var historyLow) || historyLow.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (historyLow.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        return new ItadHistoryLow(
            ToAmount(historyLow, "all"),
            ToAmount(historyLow, "y1"),
            ToAmount(historyLow, "m3"));
    }

    /// <summary>
    /// Reads one optional history-low bucket. Absent or JSON null means "not reported". A present but
    /// malformed bucket is rejected instead of collapsing to null, so a broken historical low does
    /// not read as an authoritative "no history".
    /// </summary>
    private static ItadAmount? ToAmount(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        var amountMinor = ReadOptionalAmount(GetElement(value, "amountInt"));
        var currency = ReadOptionalCurrency(ReadOptionalString(value, "currency"));
        return amountMinor is null && currency is null
            ? null
            : new ItadAmount(amountMinor, currency);
    }

    /// <summary>
    /// Reads the optional deal timestamp. Absent or JSON null returns <see langword="null"/> so the
    /// caller keeps its current fallback; a present value must be a non-blank, parseable string,
    /// otherwise the payload is malformed and throws.
    /// </summary>
    private static DateTime? GetTimestamp(JsonElement element)
    {
        var value = ReadOptionalString(element, "timestamp");
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0 ||
            !DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new JsonException(MalformedPricesMessage);
        }

        return parsed;
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

    /// <summary>
    /// Reads an optional string at <paramref name="path"/>, trimmed. Absent or JSON null returns
    /// <see langword="null"/>; a present non-string value is malformed and throws.
    /// </summary>
    private static string? ReadOptionalString(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        if (value is not { } found || found.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (found.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(MalformedPricesMessage);
        }

        return found.GetString()?.Trim();
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
}
