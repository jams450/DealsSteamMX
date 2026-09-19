using System.Globalization;
using Deals.BusinessLogic.Exceptions;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Cover art for the library, read from Steam and persisted as a URL in <c>games.image_url</c>. Nothing
/// else is written: the pass never claims identity, never touches prices and never rewrites a library row,
/// so a wrong cover is a cosmetic mistake the user can replace with the next pick.
/// </summary>
public sealed class LibraryCoverService : ILibraryCoverService
{
    private const string SteamNamespace = "steam";

    private readonly IRepository _repository;
    private readonly ISteamStoreClient _steamStoreClient;
    private readonly ILogger<LibraryCoverService> _logger;

    public LibraryCoverService(
        IRepository repository,
        ISteamStoreClient steamStoreClient,
        ILogger<LibraryCoverService> logger)
    {
        _repository = repository;
        _steamStoreClient = steamStoreClient;
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
            return new LibraryCoverSyncResult(0, 0, 0, 0, 0);
        }

        // A cover is never replaced here: the pass only fills what is empty, so a manual pick survives.
        var missingGameIds = await _repository.Get<Game>()
            .Where(game => gameIds.Contains(game.GameId) && (game.ImageUrl == null || game.ImageUrl == ""))
            .Select(game => game.GameId)
            .ToListAsync(cancellationToken);

        var appIdByGameId = await ResolveSteamAppIdsAsync(missingGameIds, cancellationToken);
        var batch = missingGameIds.Where(appIdByGameId.ContainsKey).Take(limit).ToList();

        var updated = 0;
        var failed = 0;

        if (batch.Count > 0)
        {
            var tracked = await _repository.GetTrack<Game>()
                .Where(game => batch.Contains(game.GameId))
                .ToListAsync(cancellationToken);

            foreach (var game in tracked)
            {
                var imageUrl = await FetchCoverUrlAsync(appIdByGameId[game.GameId], cancellationToken);
                if (imageUrl is null)
                {
                    failed++;
                    continue;
                }

                game.ImageUrl = imageUrl;
                updated++;
            }

            // One save for the whole pass: a failure halfway still lands every cover fetched so far.
            if (updated > 0)
            {
                await _repository.SaveChangesAsync();
            }
        }

        var solvable = appIdByGameId.Count;
        return new LibraryCoverSyncResult(
            missingGameIds.Count,
            missingGameIds.Count - solvable,
            updated,
            failed,
            solvable - updated - failed);
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
    /// Header image of an appid, or null when Steam has nothing to give: a delisted game, a request that
    /// failed or an appid without artwork are all the same outcome for a cover sync, and none of them may
    /// abort the pass.
    /// </summary>
    private async Task<string?> FetchCoverUrlAsync(int appId, CancellationToken cancellationToken)
    {
        try
        {
            var details = await _steamStoreClient.GetAppDetailsAsync(appId, cancellationToken);
            var imageUrl = details?.ImageUrl;
            return string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Steam returned no cover for appid {AppId}", appId);
            return null;
        }
    }
}
