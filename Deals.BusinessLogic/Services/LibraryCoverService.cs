using System.Globalization;
using Deals.BusinessLogic.Exceptions;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Cover art for the library, resolved through a provider chain and persisted as a URL in
/// <c>games.image_url</c>. Nothing else is written: the pass never claims identity, never touches prices and
/// never rewrites a library row, so a wrong cover is a cosmetic mistake the user can replace with the next
/// pick.
///
/// The chain, per game, stops at the first source that yields a URL: Steam by an appid the catalog already
/// knows, Steam by title, IGDB by title, SteamGridDB. Its order follows the game's stores — a game with any
/// canonical store key is a PC game, so Steam goes first; a game whose stores are only open platform slugs
/// is a console game, so IGDB goes first — which mirrors <c>resolveCoverSource</c> in
/// <c>Deals.Web/lib/contracts/library-covers.ts</c> so the automatic pass and the manual picker agree about
/// which catalogue is the natural home of a game. The pass visits the games whose appid is already known
/// first, so a bounded click spends its cheapest wins before its expensive guesses.
///
/// Identity is never claimed here, and the reason is not cosmetic: writing a <c>game_external_ids</c> row
/// with namespace <c>steam</c> would make <c>LibraryPriceBindingService</c> start pricing that game from
/// Steam, which is a decision a title match cannot make. Only <c>games.image_url</c> moves.
/// </summary>
public sealed class LibraryCoverService : ILibraryCoverService
{
    private const string SteamNamespace = "steam";

    private readonly IRepository _repository;
    private readonly ISteamStoreClient _steamStoreClient;
    private readonly IManualSearchService _manualSearchService;
    private readonly ISteamGridDbClient _steamGridDbClient;
    private readonly ILogger<LibraryCoverService> _logger;

    public LibraryCoverService(
        IRepository repository,
        ISteamStoreClient steamStoreClient,
        IManualSearchService manualSearchService,
        ISteamGridDbClient steamGridDbClient,
        ILogger<LibraryCoverService> logger)
    {
        _repository = repository;
        _steamStoreClient = steamStoreClient;
        _manualSearchService = manualSearchService;
        _steamGridDbClient = steamGridDbClient;
        _logger = logger;
    }

    public async Task<LibraryCoverSyncResult> SyncMissingCoversAsync(
        int userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var gameIds = await _repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId && row.GameId != null)
            .Select(row => row.GameId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (gameIds.Count == 0)
        {
            return new LibraryCoverSyncResult(0, 0, 0, 0, 0, 0, 0, 0);
        }

        // A cover is never replaced here: the pass only fills what is empty, so a manual pick survives.
        var missingGameIds = await _repository.Get<Game>()
            .Where(game => gameIds.Contains(game.GameId) && (game.ImageUrl == null || game.ImageUrl == ""))
            .Select(game => game.GameId)
            .ToListAsync(cancellationToken);

        if (missingGameIds.Count == 0)
        {
            return new LibraryCoverSyncResult(0, 0, 0, 0, 0, 0, 0, 0);
        }

        var appIdByGameId = await ResolveSteamAppIdsAsync(missingGameIds, cancellationToken);

        // Cheapest first: a game the catalog already links to a Steam appid needs one request to resolve,
        // while a game without one costs a title search at best. A bounded pass must spend its limit on the
        // cheap wins before it spends it on guesses.
        var batch = missingGameIds
            .OrderByDescending(gameId => appIdByGameId.ContainsKey(gameId))
            .ThenBy(gameId => gameId)
            .Take(limit)
            .ToList();

        var updatedBySteam = 0;
        var updatedByIgdb = 0;
        var updatedBySteamGridDb = 0;
        var unmatched = 0;
        var failed = 0;

        if (batch.Count > 0)
        {
            var tracked = await _repository.GetTrack<Game>()
                .Where(game => batch.Contains(game.GameId))
                .ToListAsync(cancellationToken);
            var trackedById = tracked.ToDictionary(game => game.GameId);

            var consoleOnlyGameIds = await ResolveConsoleOnlyAsync(userId, batch, cancellationToken);

            foreach (var gameId in batch)
            {
                if (!trackedById.TryGetValue(gameId, out var game))
                {
                    continue;
                }

                var attempt = await ResolveCoverAsync(
                    game,
                    appIdByGameId,
                    consoleOnlyGameIds.Contains(gameId),
                    cancellationToken);

                if (attempt.Url is null)
                {
                    if (attempt.Failed)
                    {
                        failed++;
                    }
                    else
                    {
                        unmatched++;
                    }

                    continue;
                }

                game.ImageUrl = attempt.Url;
                switch (attempt.Origin)
                {
                    case CoverOrigin.Steam:
                        updatedBySteam++;
                        break;
                    case CoverOrigin.Igdb:
                        updatedByIgdb++;
                        break;
                    case CoverOrigin.SteamGridDb:
                        updatedBySteamGridDb++;
                        break;
                }
            }

            // One save for the whole pass: a failure halfway still lands every cover fetched so far.
            if (updatedBySteam + updatedByIgdb + updatedBySteamGridDb > 0)
            {
                await _repository.SaveChangesAsync();
            }
        }

        return new LibraryCoverSyncResult(
            missingGameIds.Count,
            updatedBySteam + updatedByIgdb + updatedBySteamGridDb,
            updatedBySteam,
            updatedByIgdb,
            updatedBySteamGridDb,
            unmatched,
            failed,
            missingGameIds.Count - batch.Count);
    }

    public async Task<string> SetCoverFromSteamAsync(long gameId, int steamAppId, CancellationToken cancellationToken = default)
    {
        if (steamAppId <= 0)
        {
            throw new ArgumentException("El appid de Steam debe ser mayor que cero.", nameof(steamAppId));
        }

        var game = await _repository.GetTrack<Game>()
            .FirstOrDefaultAsync(candidate => candidate.GameId == gameId, cancellationToken)
            ?? throw new GameNotFoundException(gameId);

        var imageUrl = await FetchCoverUrlAsync(steamAppId, cancellationToken);
        if (imageUrl is null)
        {
            throw new ArgumentException("Steam no devolvió portada para ese appid.", nameof(steamAppId));
        }

        // Aquí sí se reemplaza: la elección fue explícita, no una pasada automática.
        game.ImageUrl = imageUrl;
        await _repository.SaveChangesAsync();
        return imageUrl;
    }

    public async Task<string> SetCoverFromIgdbAsync(
        long gameId,
        long igdbId,
        CancellationToken cancellationToken = default)
    {
        if (igdbId <= 0)
        {
            throw new ArgumentException("El id de IGDB debe ser mayor que cero.", nameof(igdbId));
        }

        // The provider is re-read before any transaction opens: no external HTTP while a transaction (and the
        // connections it holds) is open, exactly like the title edit and the manual add. Unavailable and
        // not-found abort here, so a failed lookup cannot reach a write.
        var lookup = await _manualSearchService.ArtworkLookupAsync(igdbId, cancellationToken);
        if (lookup.Source is null)
        {
            throw new GameCoverSourceUnavailableException();
        }

        var imageUrl = lookup.Artwork?.Url;
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new GameCoverSourceNotFoundException(igdbId);
        }

        return await _repository.ExecuteInTransactionAsync(async () =>
        {
            // The target is re-read under the transaction: a concurrent merge cannot have absorbed it since
            // the provider read, and a missing row is a 404 with nothing written.
            var game = await _repository.GetTrack<Game>()
                .FirstOrDefaultAsync(candidate => candidate.GameId == gameId, cancellationToken)
                ?? throw new GameNotFoundException(gameId);

            // Only the display column moves. No identity mapping, no title, no release year, no library row:
            // the pick says which art to paint, never what the game is.
            game.ImageUrl = imageUrl;
            await _repository.SaveChangesAsync();

            _logger.LogInformation("Cover of game {GameId} selected from IGDB", game.GameId);
            return imageUrl;
        });
    }

    public async Task<string> SetCoverFromSteamGridDbAsync(
        long gameId,
        int steamGridDbId,
        CancellationToken cancellationToken = default)
    {
        if (steamGridDbId <= 0)
        {
            throw new ArgumentException("El id de SteamGridDB debe ser mayor que cero.", nameof(steamGridDbId));
        }

        // The provider is re-read before any transaction opens: no external HTTP while a transaction (and the
        // connections it holds) is open, exactly like the title edit and the manual add. Unavailable and
        // not-found abort here, so a failed lookup cannot reach a write.
        var lookup = await _steamGridDbClient.GetCoverUrlByIdAsync(steamGridDbId, cancellationToken);
        if (lookup.Source is null)
        {
            throw new GameCoverSourceUnavailableException();
        }

        var imageUrl = lookup.Url;
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new GameCoverSourceNotFoundException(steamGridDbId);
        }

        return await _repository.ExecuteInTransactionAsync(async () =>
        {
            // The target is re-read under the transaction: a concurrent merge cannot have absorbed it since
            // the provider read, and a missing row is a 404 with nothing written.
            var game = await _repository.GetTrack<Game>()
                .FirstOrDefaultAsync(candidate => candidate.GameId == gameId, cancellationToken)
                ?? throw new GameNotFoundException(gameId);

            // Only the display column moves. No identity mapping, no title, no release year, no library row:
            // the pick says which art to paint, never what the game is.
            game.ImageUrl = imageUrl;
            await _repository.SaveChangesAsync();

            _logger.LogInformation("Cover of game {GameId} selected from SteamGridDB", game.GameId);
            return imageUrl;
        });
    }

    /// <summary>
    /// Walks the chain for one game and returns the first URL it finds. The order is the game's stores', not a
    /// preference of this service: a PC game (any canonical store key, alone or mixed with a console) tries
    /// Steam first, and a console game (only open platform slugs, so no canonical key at all) tries IGDB
    /// first. Both end on the same three sources, so the difference is the order, never the coverage.
    /// </summary>
    private async Task<CoverAttempt> ResolveCoverAsync(
        Game game,
        IReadOnlyDictionary<long, int> appIdByGameId,
        bool consoleOnly,
        CancellationToken cancellationToken)
    {
        var steps = consoleOnly
            ? new[] { CoverStep.Igdb, CoverStep.SteamGridDb, CoverStep.SteamAppId, CoverStep.SteamTitle }
            : new[] { CoverStep.SteamAppId, CoverStep.SteamTitle, CoverStep.Igdb, CoverStep.SteamGridDb };

        var failed = false;
        foreach (var step in steps)
        {
            var (url, stepFailed) = step switch
            {
                CoverStep.SteamAppId => await TrySteamByAppIdAsync(game, appIdByGameId, cancellationToken),
                CoverStep.SteamTitle => await TrySteamByTitleAsync(game, cancellationToken),
                CoverStep.Igdb => await TryIgdbByTitleAsync(game, cancellationToken),
                _ => await TrySteamGridDbAsync(game, cancellationToken)
            };

            failed |= stepFailed;
            if (!string.IsNullOrWhiteSpace(url))
            {
                return new CoverAttempt(url, OriginOf(step), false);
            }
        }

        return new CoverAttempt(null, CoverOrigin.None, failed);
    }

    /// <summary>
    /// Steam header image of an appid the catalog already knows for this game. A game without a known appid
    /// skips the step: that is neither a failure nor a miss, there was nothing to ask.
    /// </summary>
    private async Task<(string? Url, bool Failed)> TrySteamByAppIdAsync(
        Game game,
        IReadOnlyDictionary<long, int> appIdByGameId,
        CancellationToken cancellationToken)
    {
        if (!appIdByGameId.TryGetValue(game.GameId, out var appId))
        {
            return (null, false);
        }

        return await TrySteamAppIdAsync(appId, cancellationToken);
    }

    /// <summary>
    /// Steam by title, then the header image of the appid the search returns. The candidate is accepted only
    /// when <see cref="StoreTitleMatcher.Matches"/> says it is the same product, and the first exact match
    /// wins: a wrong appid would paint another game's art, and this search runs with <c>cc=mx&amp;l=spanish</c>,
    /// so a localised candidate name will not match a canonical English title at all (a false negative, never
    /// a wrong cover).
    /// </summary>
    private async Task<(string? Url, bool Failed)> TrySteamByTitleAsync(Game game, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(game.Title))
        {
            return (null, false);
        }

        IReadOnlyList<SteamSearchResult> candidates;
        try
        {
            candidates = await _steamStoreClient.SearchAsync(game.Title, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Steam search returned no candidates for game {GameId}", game.GameId);
            return (null, true);
        }

        var appId = candidates
            .Where(candidate => StoreTitleMatcher.Matches(candidate.Name, game.Title))
            .Select(candidate => candidate.AppId)
            .FirstOrDefault();
        return appId > 0
            ? await TrySteamAppIdAsync(appId, cancellationToken)
            : (null, false);
    }

    /// <summary>
    /// IGDB by title. An exact title match is enough here and this is deliberate: IGDB can return several rows
    /// for the same title across platforms, and a wrong-causality match is cosmetic only — nothing but
    /// <c>games.image_url</c> is written — so the pass does not claim identity by picking one. Blank covers
    /// are skipped rather than accepted, because an empty URL would look like a solved game forever.
    /// </summary>
    private async Task<(string? Url, bool Failed)> TryIgdbByTitleAsync(Game game, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(game.Title))
        {
            return (null, false);
        }

        ManualSearchResult result;
        try
        {
            // The IGDB client's contract says it never throws, but its transport call is unguarded; the pass
            // wraps it anyway, because no single provider may abort a bounded sweep.
            result = await _manualSearchService.SearchAsync(game.Title, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "IGDB search returned no candidates for game {GameId}", game.GameId);
            return (null, true);
        }

        if (result.Source is null)
        {
            return (null, true);
        }

        var url = result.Hits
            .Where(hit => !string.IsNullOrWhiteSpace(hit.ImageUrl) && StoreTitleMatcher.Matches(hit.Title, game.Title))
            .Select(hit => hit.ImageUrl)
            .FirstOrDefault();
        return (url, false);
    }

    /// <summary>
    /// SteamGridDB, the last source. Its client already resolves the exact title match and the best-scoring
    /// vertical grid, so this step only separates "could not be consulted" from "answered without art".
    /// </summary>
    private async Task<(string? Url, bool Failed)> TrySteamGridDbAsync(Game game, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(game.Title))
        {
            return (null, false);
        }

        var lookup = await _steamGridDbClient.LookupCoverUrlAsync(game.Title, cancellationToken);
        return lookup.Source is null
            ? (null, true)
            : (lookup.Url, false);
    }

    /// <summary>
    /// Header image of an appid. The failure flag is what tells "Steam answered and has no art" (a delisted
    /// game or an appid without a header) apart from "Steam could not be consulted at all": the first is a
    /// miss, the second is a broken pass segment, and the report counts them in different columns.
    /// </summary>
    private async Task<(string? Url, bool Failed)> TrySteamAppIdAsync(int appId, CancellationToken cancellationToken)
    {
        try
        {
            var details = await _steamStoreClient.GetAppDetailsAsync(appId, cancellationToken);
            var imageUrl = details?.ImageUrl;
            return (string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Steam returned no cover for appid {AppId}", appId);
            return (null, true);
        }
    }

    /// <summary>
    /// Games of the batch whose stores carry no canonical key, so they are console games: their store values
    /// are open platform slugs alone. A game with several rows is console-only only when <em>none</em> of them
    /// is a canonical store key — one PC row is enough to make it a PC game.
    /// </summary>
    private async Task<HashSet<long>> ResolveConsoleOnlyAsync(
        int userId,
        IReadOnlyCollection<long> gameIds,
        CancellationToken cancellationToken)
    {
        var consoleOnly = new HashSet<long>();
        if (gameIds.Count == 0)
        {
            return consoleOnly;
        }

        var rows = await _repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId && row.GameId != null && gameIds.Contains(row.GameId.Value))
            .Select(row => new { GameId = row.GameId!.Value, row.Store })
            .ToListAsync(cancellationToken);

        foreach (var group in rows.GroupBy(row => row.GameId))
        {
            if (!group.Any(row => StoreKeys.IsKnown(row.Store)))
            {
                consoleOnly.Add(group.Key);
            }
        }

        return consoleOnly;
    }

    /// <summary>
    /// Steam appid the catalog already knows for each game. Missing ids are simply absent from the map, and
    /// a game with several Steam links keeps the first one: the pass needs a cover, not a decision.
    /// </summary>
    private async Task<Dictionary<long, int>> ResolveSteamAppIdsAsync(
        IReadOnlyCollection<long> gameIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, int>();
        if (gameIds.Count == 0)
        {
            return map;
        }

        var links = await _repository.Get<GameExternalId>()
            .Where(link => link.NamespaceName == SteamNamespace && gameIds.Contains(link.GameId))
            .Select(link => new { link.GameId, link.ExternalId })
            .ToListAsync(cancellationToken);

        foreach (var link in links)
        {
            if (!map.ContainsKey(link.GameId) &&
                int.TryParse(link.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) &&
                appId > 0)
            {
                map[link.GameId] = appId;
            }
        }

        return map;
    }

    /// <summary>
    /// Header image of one appid for the manual pick, or null when Steam has nothing to give. The manual path
    /// reports both outcomes as one: the user asked for one appid and either it has art or it does not.
    /// </summary>
    private async Task<string?> FetchCoverUrlAsync(int appId, CancellationToken cancellationToken) =>
        (await TrySteamAppIdAsync(appId, cancellationToken)).Url;

    private static CoverOrigin OriginOf(CoverStep step) => step switch
    {
        CoverStep.SteamAppId or CoverStep.SteamTitle => CoverOrigin.Steam,
        CoverStep.Igdb => CoverOrigin.Igdb,
        _ => CoverOrigin.SteamGridDb
    };

    private enum CoverStep
    {
        SteamAppId,
        SteamTitle,
        Igdb,
        SteamGridDb
    }

    private enum CoverOrigin
    {
        None,
        Steam,
        Igdb,
        SteamGridDb
    }

    private readonly record struct CoverAttempt(string? Url, CoverOrigin Origin, bool Failed);
}
