using System.Globalization;
using System.Net;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Steam wishlist client. Everything about the payload is treated as untrusted: the outcome is always a
/// typed status, and odd data degrades instead of throwing.
/// </summary>
public sealed class SteamWishlistClient(HttpClient httpClient) : ISteamWishlistClient
{
    private const string WishlistPath = "IWishlistService/GetWishlist/v1?steamid=";
    private const string ResultHeader = "x-eresult";
    private const string ResponseProperty = "response";
    private const string ItemsProperty = "items";

    // Valve EResult codes that matter here: 1 = OK, 15 = AccessDenied (private or unknown profile),
    // 25 = LimitExceeded and 84 = RateLimitExceeded are the rate-limit pair.
    private const int ResultOk = 1;
    private const int ResultAccessDenied = 15;
    private const int ResultRateLimited = 25;
    private const int ResultBanned = 84;

    // date_added is a uint32 count of seconds: anything outside the Unix-time range is discarded.
    private const long MaxUnixSeconds = 253402300799; // 9999-12-31T23:59:59Z

    public async Task<SteamWishlistResult> GetWishlistAsync(string steamId64, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(steamId64))
        {
            throw new ArgumentException("SteamID64 is required.", nameof(steamId64));
        }

        // The steamid travels in the query string because that is the only supported location. The URL is
        // never logged nor echoed into an exception message.
        var path = $"{WishlistPath}{Uri.EscapeDataString(steamId64)}";
        using var response = await httpClient.GetAsync(path, cancellationToken);
        var eResult = ReadEResult(response);

        if (response.StatusCode == HttpStatusCode.TooManyRequests || eResult is ResultRateLimited or ResultBanned)
        {
            return SteamWishlistResult.RateLimited();
        }

        if (!response.IsSuccessStatusCode)
        {
            return SteamWishlistResult.Failed();
        }

        if (eResult == ResultAccessDenied)
        {
            // Private profile or wishlist. The body cannot tell this apart from an empty wishlist, which
            // is exactly why the header decides.
            return SteamWishlistResult.Inaccessible();
        }

        if (eResult is not null && eResult != ResultOk)
        {
            return SteamWishlistResult.Failed();
        }

        return await ParseResponseAsync(response, eResult == ResultOk, cancellationToken);
    }

    private static async Task<SteamWishlistResult> ParseResponseAsync(
        HttpResponseMessage response,
        bool confirmedOk,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return SteamWishlistResult.Failed();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(ResponseProperty, out var body) ||
                body.ValueKind != JsonValueKind.Object)
            {
                return SteamWishlistResult.Failed();
            }

            if (!body.TryGetProperty(ItemsProperty, out var items))
            {
                // items is omitted when it is empty. That is only a legitimate empty wishlist when the
                // header said OK; without it, private and empty are indistinguishable, so it must not be
                // reported as a real zero.
                return confirmedOk ? SteamWishlistResult.Ok([]) : SteamWishlistResult.Failed();
            }

            if (items.ValueKind != JsonValueKind.Array)
            {
                return SteamWishlistResult.Failed();
            }

            var parsed = new List<SteamWishlistItem>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && TryParseItem(item, out var entry))
                {
                    parsed.Add(entry);
                }
            }

            return SteamWishlistResult.Ok(parsed);
        }
    }

    private static bool TryParseItem(JsonElement item, out SteamWishlistItem entry)
    {
        entry = null!;

        // appid is the only field a persisted row can be built from, so an entry without a usable one is
        // skipped rather than failing the whole wishlist.
        if (!item.TryGetProperty("appid", out var appIdElement) ||
            appIdElement.ValueKind != JsonValueKind.Number ||
            !appIdElement.TryGetInt32(out var appId) ||
            appId <= 0)
        {
            return false;
        }

        // All numeric values, never strings, but a bad priority only drops the field, not the item.
        int? priority = item.TryGetProperty("priority", out var priorityElement) &&
                        priorityElement.ValueKind == JsonValueKind.Number &&
                        priorityElement.TryGetInt32(out var parsedPriority)
            ? parsedPriority
            : null;

        DateTimeOffset? addedAt = null;
        if (item.TryGetProperty("date_added", out var dateElement) &&
            dateElement.ValueKind == JsonValueKind.Number &&
            dateElement.TryGetInt64(out var seconds) &&
            seconds is >= 0 and <= MaxUnixSeconds)
        {
            addedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        entry = new SteamWishlistItem(appId, priority, addedAt);
        return true;
    }

    private static int? ReadEResult(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(ResultHeader, out var values))
        {
            return null;
        }

        return int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
