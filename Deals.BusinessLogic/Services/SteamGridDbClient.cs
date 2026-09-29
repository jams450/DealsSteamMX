using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// SteamGridDB v2 client: the last source of the automatic cover chain (<c>docs/PLAN_LIBRARY.md</c> §9).
/// Two sequential reads per game, no parallelism and no retries:
/// <list type="number">
/// <item><c>GET /search/autocomplete/{term}</c> turns a title into a game id.</item>
/// <item><c>GET /grids/game/{gameId}?dimensions=600x900&amp;types=static</c> returns that game's grids.</item>
/// </list>
///
/// The same two endpoints back the three public reads: <see cref="LookupCoverUrlAsync"/> chains them for the
/// automatic pass, while <see cref="SearchAsync"/> and <see cref="GetCoverUrlByIdAsync"/> expose one step each,
/// so the manual picker can search by title, list the candidates and resolve the URL of the id the user chose —
/// the autocomplete payload carries no artwork, so the second request cannot be skipped.
///
/// Constants verified against SteamGridDB's own OpenAPI spec
/// (<c>https://www.steamgriddb.com/static/openapi.yml</c>, <c>version: 2.10.0</c>) and its official wrapper
/// (<c>https://github.com/SteamGridDB/node-steamgriddb</c>):
/// <list type="bullet">
/// <item>Base URL <c>https://www.steamgriddb.com/api/v2</c>; auth is <c>Authorization: Bearer &lt;key&gt;</c>
/// (the spec's <c>securitySchemes</c> entry is literally named "API Key"). The key travels in a header, so it
/// never reaches a request URL — the only thing HttpClient may log.</item>
/// <item>Autocomplete answers <c>{ success: bool, data: [ { id, name, types, verified } ] }</c>. Its
/// <c>release_date</c> field exists in the wrapper but not in the spec: undocumented, so this client does not
/// read it.</item>
/// <item>Grids answers <c>{ success, page, total, limit, data: [ Grid ] }</c> with <c>Grid</c> carrying
/// <c>id</c>, <c>score</c> (integer), <c>style</c>, <c>url</c> ("Path to full size grid image"), <c>thumb</c>,
/// <c>width</c>, <c>height</c>, <c>mime</c>, <c>upvotes</c>, <c>downvotes</c> and <c>author</c>.</item>
/// <item>Query params: <c>styles</c>, <c>dimensions</c> (comma-delimited; the enum includes <c>460x215</c>,
/// <c>920x430</c>, <c>600x900</c>, <c>342x482</c>, <c>660x930</c>, <c>512x512</c>, <c>1024x1024</c>),
/// <c>mimes</c>, <c>types</c> (documented default <c>static</c>), <c>nsfw</c>, <c>humor</c>, <c>epilepsy</c>,
/// <c>oneoftag</c>, <c>limit</c> (default 50; values &gt; 50 are ignored) and <c>page</c> (default 0).</item>
/// </list>
///
/// The spec declares <b>no default for <c>dimensions</c></b>, so it is always sent explicitly: the library
/// grid is vertical, and omitting it would let the provider decide the aspect ratio of every cover.
///
/// Rate limits, ToS and attribution are <b>undocumented</b> (the ToS page is a JS-rendered shell, and the
/// spec has no <c>429</c> or rate-limit text at all). That is the reason there is no parallelism and no
/// retry anywhere in this client: one sequential request per step, inside a pass that is already bounded.
/// Reading a limit that is not published and then designing against it is how a provider bans an IP.
///
/// Credentials: <c>STEAMGRIDDB_API_KEY</c>, exactly like IGDB's flat keys (<c>IGDB_CLIENT_ID</c>…), because
/// this is a per-provider secret and not a storefront setting. Missing or blank means "unavailable": the
/// client answers, it never throws and it never logs the key.
/// </summary>
public sealed class SteamGridDbClient : ISteamGridDbClient
{
    private const string Source = "steamgriddb";
    private const string ApiKeySetting = "STEAMGRIDDB_API_KEY";
    private const string AuthenticationScheme = "Bearer";

    // The pass is bounded twice: once by the number of games (LibraryCoverLimits) and once here, per
    // response, so a hostile payload cannot inflate memory. Both caps are far above what a legitimate
    // answer needs — the autocomplete list is a shortlist and 50 is the provider's own default page size.
    private const int MaxCandidates = 20;
    private const int MaxGrids = 50;
    private const int MaxTermLength = 128;

    // Hard ceiling on the bytes parsed from one answer. A 50-entry page is a few tens of kilobytes at most,
    // so a megabyte is generous for any legitimate answer and still bounds what a hostile one can allocate.
    private const int MaxResponseBytes = 1 << 20;

    // The library grid is vertical; declared here because the spec publishes no default for `dimensions`.
    private const string GridDimensions = "600x900";

    // Only static art: an animated grid is a video-like asset the grid column cannot use as a cover.
    private const string GridTypes = "static";

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SteamGridDbClient> _logger;

    public SteamGridDbClient(
        HttpClient http,
        IConfiguration configuration,
        ILogger<SteamGridDbClient> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SteamGridDbCoverLookupResult> LookupCoverUrlAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var search = await SearchAsync(title, cancellationToken);
        if (search.Source is null)
        {
            // "Could not be consulted" and "answered without art" are different answers, and the cover pass
            // counts them in different columns, so the unavailable case short-circuits here instead of sliding
            // into the empty result of a lookup that never happened.
            return new SteamGridDbCoverLookupResult(null, null);
        }

        var gameId = PickBestGameId(search.Candidates, NormalizeTerm(title));
        if (gameId is null)
        {
            return new SteamGridDbCoverLookupResult(Source, null);
        }

        return await GetCoverUrlByIdAsync(gameId.Value, cancellationToken);
    }

    public async Task<SteamGridDbSearchResult> SearchAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var term = NormalizeTerm(title);
        if (term.Length == 0)
        {
            // Nothing to ask. The provider "answered" for a title it cannot have, so this is not-found and
            // not an outage — the same shape a real title with no game returns.
            return new SteamGridDbSearchResult(Source, []);
        }

        var apiKey = _configuration[ApiKeySetting];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new SteamGridDbSearchResult(null, []);
        }

        using var document = await SendAsync($"search/autocomplete/{Uri.EscapeDataString(term)}", apiKey, cancellationToken);
        if (document is null)
        {
            return new SteamGridDbSearchResult(null, []);
        }

        var root = document.RootElement;
        if (!IsSuccess(root) || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new SteamGridDbSearchResult(null, []);
        }

        var candidates = new List<SteamGridDbGameCandidate>();
        var inspected = 0;
        foreach (var element in data.EnumerateArray())
        {
            if (inspected >= MaxCandidates)
            {
                break;
            }

            inspected++;
            var candidate = ToCandidate(element);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        // A non-empty list where nothing parsed is not "this title has no game": it is a payload that is not
        // the documented shape, and the honest answer for that is "unavailable", never "no art".
        if (candidates.Count == 0 && inspected > 0)
        {
            return new SteamGridDbSearchResult(null, []);
        }

        return new SteamGridDbSearchResult(Source, candidates);
    }

    public async Task<SteamGridDbCoverLookupResult> GetCoverUrlByIdAsync(
        int gameId,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration[ApiKeySetting];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new SteamGridDbCoverLookupResult(null, null);
        }

        if (gameId <= 0)
        {
            // An impossible id: the provider "answered" for a game it cannot have, without spending a request.
            return new SteamGridDbCoverLookupResult(Source, null);
        }

        var path = $"grids/game/{gameId}?dimensions={GridDimensions}&types={GridTypes}";
        using var document = await SendAsync(path, apiKey, cancellationToken);
        if (document is null)
        {
            return new SteamGridDbCoverLookupResult(null, null);
        }

        var root = document.RootElement;
        if (!IsSuccess(root) || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new SteamGridDbCoverLookupResult(null, null);
        }

        var grids = new List<SteamGridDbGrid>();
        var inspected = 0;
        foreach (var element in data.EnumerateArray())
        {
            if (inspected >= MaxGrids)
            {
                break;
            }

            inspected++;
            var grid = ToGrid(element);
            if (grid is not null)
            {
                grids.Add(grid);
            }
        }

        // Same rule as the autocomplete: a non-empty list where nothing parsed is a malformed payload, not a
        // game without art.
        if (grids.Count == 0 && inspected > 0)
        {
            return new SteamGridDbCoverLookupResult(null, null);
        }

        // "Best" is the documented `score`; ties keep the provider's own order, so an all-zero result falls
        // back to the first grid instead of a random one.
        var url = grids
            .OrderByDescending(grid => grid.Score)
            .Select(grid => grid.Url)
            .FirstOrDefault();
        return new SteamGridDbCoverLookupResult(Source, url);
    }

    /// <summary>
    /// One request. Null means "unavailable": no key, transport failure, cancellation of the request (not of
    /// the caller), non-2xx or a payload that does not parse. The caller's own cancellation is rethrown — a
    /// pass that the user cancelled must stop, it must not look like a provider outage.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(string path, string apiKey, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            // The key is a header: it must never be appended to the URL, because that is the one part of the
            // request HttpClient logs.
            request.Headers.TryAddWithoutValidation("Authorization", $"{AuthenticationScheme} {apiKey}");
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "SteamGridDB no respondió a la consulta de portada.");
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Status only: the path is safe to log, the header never is.
                _logger.LogWarning("SteamGridDB devolvió {Status} en {Path}", (int)response.StatusCode, path);
                return null;
            }

            try
            {
                return await ReadBoundedAsync(response, path, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException)
            {
                // A payload that is not the documented shape is a malformed answer, never a cover.
                _logger.LogWarning(exception, "SteamGridDB devolvió una carga ilegible.");
                return null;
            }
        }
    }

    /// <summary>
    /// Parses the body with a hard byte ceiling. The per-list caps above bound how many entries are inspected,
    /// but they cannot bound the parse: <see cref="JsonDocument"/> materializes the whole document, so a
    /// hostile or broken response would inflate memory before any cap applies. A declared length over the
    /// ceiling is refused before parsing, and a chunked body is counted as it is read.
    /// <para>
    /// Scope of the ceiling, stated so nobody reads more into it: the request uses the default completion
    /// option, so HttpClient has already buffered the whole body by the time this runs. The ceiling bounds
    /// what is <b>parsed</b>, not what is transferred; it stops a hostile document from becoming objects, it
    /// does not stop the bytes from arriving.
    /// </para>
    /// </summary>
    private async Task<JsonDocument?> ReadBoundedAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            _logger.LogWarning("SteamGridDB devolvió una carga demasiado grande en {Path}", path);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                _logger.LogWarning("SteamGridDB devolvió una carga demasiado grande en {Path}", path);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private static bool IsSuccess(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("success", out var success) &&
        success.ValueKind == JsonValueKind.True;

    private static SteamGridDbGameCandidate? ToCandidate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var id) || !id.TryGetInt32(out var gameId) || gameId <= 0)
        {
            return null;
        }

        var name = ReadString(element, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var verified = element.TryGetProperty("verified", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new SteamGridDbGameCandidate(gameId, name, verified);
    }

    private static SteamGridDbGrid? ToGrid(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var id) || !id.TryGetInt32(out var gridId) || gridId <= 0)
        {
            return null;
        }

        var score = element.TryGetProperty("score", out var scoreElement) && scoreElement.TryGetInt32(out var value)
            ? value
            : 0;

        var url = ReadString(element, "url");
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Only an absolute HTTPS URL is stored: a relative or non-HTTPS value would paint a broken or
            // insecure image, and a cover is exactly the kind of field a hostile payload can aim at.
            return null;
        }

        return new SteamGridDbGrid(gridId, score, url);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Term as the provider sees it: trimmed, and truncated at <see cref="MaxTermLength"/>. Shared by the
    /// request and by the title comparison of <see cref="LookupCoverUrlAsync"/>, so both read the same text.
    /// </summary>
    private static string NormalizeTerm(string? title)
    {
        var term = (title ?? string.Empty).Trim();
        return term.Length > MaxTermLength ? term[..MaxTermLength] : term;
    }

    /// <summary>
    /// Game id of the first exact title match, preferring a verified entry. The comparison is the repository's
    /// own guard (<see cref="StoreTitleMatcher"/>), so this step cannot accept a title that merely looks
    /// similar — the same rule Steam's title search follows. Among the exact matches a verified row wins, and
    /// the first one when none is verified: a verified entry is the game's own record, the rest are community
    /// aliases that may point at a remake.
    /// </summary>
    private static int? PickBestGameId(IReadOnlyList<SteamGridDbGameCandidate> candidates, string term) =>
        candidates
            .Where(candidate => StoreTitleMatcher.Matches(candidate.Name, term))
            .OrderByDescending(candidate => candidate.Verified)
            .Select(candidate => (int?)candidate.Id)
            .FirstOrDefault();

    private sealed record SteamGridDbGrid(int Id, int Score, string Url);
}
