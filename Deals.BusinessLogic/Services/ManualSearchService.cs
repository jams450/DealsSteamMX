using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// IGDB v4 client for the manual library add. Two calls: a bounded <c>search</c> for the dialog
/// (covers + years, measured 5/5 in <c>docs/PLAN_CONSOLE.md</c> §2.3) and an id lookup for the cover of a
/// row being created. Credentials: <c>IGDB_CLIENT_ID</c> is required for the <c>Client-ID</c> header;
/// auth is <c>IGDB_TOKEN</c> when present, else the <c>IGDB_CLIENT_SECRET</c> exchange cached here.
/// Measured traps (§2.3): cover URLs arrive protocol-relative (<c>//images.igdb.com/...</c>) and a title
/// search returns one row per platform — never trust row order, the dialog only displays and the id is
/// refetched. No credential is ever logged or written to the repo.
/// </summary>
public class ManualSearchService : IManualSearchService
{
    private const string GamesEndpoint = "https://api.igdb.com/v4/games";
    private const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";
    private const int MaxSearchLength = 128;
    private const int SearchLimit = 5;

    // App-level token shared by every transient client instance (an HTTP call per scope would re-exchange
    // on each search). A lost race just exchanges twice; a 401 clears it and retries once.
    private static string? s_cachedToken;
    private static DateTime s_cachedUntilUtc;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ManualSearchService> _logger;

    public ManualSearchService(
        HttpClient http,
        IConfiguration configuration,
        ILogger<ManualSearchService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ManualSearchResult> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        var query = (title ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return new ManualSearchResult(null, []);
        }

        if (query.Length > MaxSearchLength)
        {
            query = query[..MaxSearchLength];
        }

        // "search" is IGDB's relevance query; the escaped string keeps user input out of the query grammar.
        var body =
            $"search \"{Escape(query)}\"; fields name,first_release_date,cover.url,platforms.name; limit {SearchLimit};";
        var json = await QueryAsync(body, cancellationToken);
        if (json is null)
        {
            return new ManualSearchResult(null, []);
        }

        var games = DeserializeGames(json);
        if (games is null)
        {
            return new ManualSearchResult(null, []);
        }

        var hits = games
            .Select(ToHit)
            .ToList();
        return new ManualSearchResult("igdb", hits);
    }

    public async Task<ManualSearchLookupResult> GameByIdAsync(
        long sourceId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId <= 0)
        {
            throw new ArgumentException("El id de IGDB debe ser mayor que cero.", nameof(sourceId));
        }

        // The id is a long interpolated directly: no client text enters the query grammar here.
        var json = await QueryAsync(
            $"where id = {sourceId}; fields name,first_release_date,cover.url,platforms.name;",
            cancellationToken);
        if (json is null)
        {
            return new ManualSearchLookupResult(null, null);
        }

        var game = DeserializeGames(json)?.FirstOrDefault();
        return game is null
            ? new ManualSearchLookupResult("igdb", null)
            : new ManualSearchLookupResult("igdb", ToHit(game));
    }

    public async Task<ManualArtwork?> ArtworkAsync(long sourceId, CancellationToken cancellationToken = default)
        => (await ArtworkLookupAsync(sourceId, cancellationToken)).Artwork;

    public async Task<ManualArtworkLookupResult> ArtworkLookupAsync(
        long sourceId,
        CancellationToken cancellationToken = default)
    {
        if (sourceId <= 0)
        {
            // Nothing to ask: the provider "answered" for an id it cannot have, so this is a not-found and
            // not an outage — the same shape a positive id with no row returns.
            return new ManualArtworkLookupResult("igdb", null);
        }

        var json = await QueryAsync($"where id = {sourceId}; fields name,first_release_date,cover.url;", cancellationToken);
        if (json is null)
        {
            return new ManualArtworkLookupResult(null, null);
        }

        var game = DeserializeGames(json)?.FirstOrDefault();
        return game is null
            ? new ManualArtworkLookupResult("igdb", null)
            : new ManualArtworkLookupResult(
                "igdb",
                new ManualArtwork(AbsoluteCover(game.Cover?.Url), YearOf(game.FirstReleaseDate)));
    }

    /// <summary>
    /// Projects one IGDB row to the display/identity shape shared by the search and the id lookup.
    /// </summary>
    private static ManualSearchHit ToHit(IgdbGame game) => new(
        game.Id,
        game.Name,
        YearOf(game.FirstReleaseDate),
        AbsoluteCover(game.Cover?.Url),
        (game.Platforms ?? [])
            .Select(platform => new ManualSearchPlatform((int)platform.Id, platform.Name))
            .ToList());

    /// <summary>Runs one IGDB query. Null means unavailable (no config, auth failure, non-2xx), logged for diagnosis.</summary>
    private async Task<string?> QueryAsync(string body, CancellationToken cancellationToken)
    {
        var clientId = _configuration["IGDB_CLIENT_ID"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var token = await GetTokenAsync(clientId, cancellationToken);
        if (token is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, GamesEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Client-ID", clientId);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized && s_cachedToken is not null)
        {
            // Stale cached token: drop it and retry once with a fresh exchange (never in a loop).
            s_cachedToken = null;
            s_cachedUntilUtc = default;
            return await QueryAsync(body, cancellationToken) is { } retried ? retried : null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // The query body is only the escaped title: safe to log, credentials are headers and never are.
            _logger.LogWarning("IGDB devolvió {Status} para: {Body}", (int)response.StatusCode, body);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string?> GetTokenAsync(string clientId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_configuration["IGDB_TOKEN"]))
        {
            return _configuration["IGDB_TOKEN"];
        }

        if (s_cachedToken is not null && DateTime.UtcNow < s_cachedUntilUtc)
        {
            return s_cachedToken;
        }

        var secret = _configuration["IGDB_CLIENT_SECRET"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var url =
            $"{TokenEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
            $"&client_secret={Uri.EscapeDataString(secret)}&grant_type=client_credentials";
        var response = await _http.PostAsync(url, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("No se pudo obtener token de IGDB/Twitch ({Status})", (int)response.StatusCode);
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("access_token", out var element))
        {
            return null;
        }

        s_cachedToken = element.GetString();
        // Twitch app tokens do not expire on their own; the cache window only avoids re-exchanging on
        // every call, and the 401 retry above covers any surprise.
        s_cachedUntilUtc = DateTime.UtcNow.AddHours(12);
        return s_cachedToken;
    }

    private static List<IgdbGame>? DeserializeGames(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<IgdbGame>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // IGDB serves protocol-relative cover URLs (measured: //images.igdb.com/igdb/image/upload/t_thumb/...).
    private static string? AbsoluteCover(string? url) => url switch
    {
        null or "" => null,
        _ when url.StartsWith("//") => $"https:{url}",
        _ when url.StartsWith("http") => url,
        _ => null
    };

    private static int? YearOf(long? unixSeconds) =>
        unixSeconds is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime.Year
            : null;

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class IgdbGame
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("first_release_date")] public long? FirstReleaseDate { get; set; }
        [JsonPropertyName("cover")] public IgdbCover? Cover { get; set; }
        [JsonPropertyName("platforms")] public List<IgdbPlatform>? Platforms { get; set; }
    }

    private sealed class IgdbCover
    {
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    }

    private sealed class IgdbPlatform
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }
}
