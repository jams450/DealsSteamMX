using System.Net;
using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Services;

public sealed class SteamStoreClient(HttpClient httpClient) : ISteamStoreClient
{
    private const string Region = "mx";
    private const string Language = "spanish";

    public async Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var uri = $"storesearch/?term={Uri.EscapeDataString(query)}&cc={Region}&l={Language}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Steam search is unavailable.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray()
            .Select(ToSearchResult)
            .Where(result => result != null)
            .Select(result => result!)
            .ToList();
    }

    public async Task<SteamGameDetails?> GetAppDetailsAsync(int appId, CancellationToken cancellationToken)
    {
        var uri = $"appdetails?appids={appId}&cc={Region}&l={Language}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Steam app details are unavailable.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty(appId.ToString(), out var app) ||
            !app.TryGetProperty("success", out var success) || !success.GetBoolean() ||
            !app.TryGetProperty("data", out var data))
        {
            return null;
        }

        var name = GetString(data, "name");
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new SteamGameDetails(
                appId,
                name,
                GetString(data, "type"),
                GetBoolean(data, "is_free"),
                GetString(data, "price_overview", "currency"),
                GetInt32(data, "price_overview", "initial"),
                GetInt32(data, "price_overview", "final"),
                GetInt32(data, "price_overview", "discount_percent"),
                Region,
                DateTime.UtcNow);
    }

    private static SteamSearchResult? ToSearchResult(JsonElement item)
    {
        var appId = GetInt32(item, "id");
        var name = GetString(item, "name");
        return appId is > 0 && !string.IsNullOrWhiteSpace(name)
            ? new SteamSearchResult(appId.Value, name, GetString(item, "type"), GetString(item, "tiny_image"))
            : null;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    private static bool GetBoolean(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        return value?.ValueKind == JsonValueKind.True;
    }

    private static int? GetInt32(JsonElement element, params string[] path)
    {
        var value = GetElement(element, path);
        return value?.TryGetInt32(out var number) == true ? number : null;
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
