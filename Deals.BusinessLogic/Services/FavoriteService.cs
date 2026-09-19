using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Favorite games of one user. Reads are one query per batch (the library page annotates a whole page at
/// once); writes are a single-row insert or delete, so unmarking twice is not an error.
/// </summary>
public class FavoriteService : IFavoriteService
{
    private readonly IRepository _repository;

    public FavoriteService(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<long>> GetFavoriteGameIdsAsync(
        int userId,
        IReadOnlyCollection<long> gameIds,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || gameIds.Count == 0)
        {
            return [];
        }

        var ids = gameIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return await _repository.Get<UserGameFavorite>()
            .Where(favorite => favorite.UserId == userId && ids.Contains(favorite.GameId))
            .Select(favorite => favorite.GameId)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> IsFavoriteForSteamAppAsync(
        int userId,
        int appId,
        CancellationToken cancellationToken = default)
    {
        var gameId = await SteamAppIdResolver.ResolveGameIdAsync(_repository, appId, cancellationToken);
        if (gameId is null)
        {
            return false;
        }

        return await _repository.Get<UserGameFavorite>()
            .AnyAsync(
                favorite => favorite.UserId == userId && favorite.GameId == gameId.Value,
                cancellationToken);
    }

    public async Task<bool> SetByGameIdAsync(
        int userId,
        long gameId,
        bool favorite,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || gameId <= 0)
        {
            return false;
        }

        var gameExists = await _repository.Get<Game>()
            .AnyAsync(game => game.GameId == gameId, cancellationToken);
        if (!gameExists)
        {
            return false;
        }

        await ApplyAsync(userId, gameId, favorite, cancellationToken);
        return true;
    }

    public async Task<bool> SetBySteamAppIdAsync(
        int userId,
        int appId,
        bool favorite,
        CancellationToken cancellationToken = default)
    {
        var gameId = await SteamAppIdResolver.ResolveGameIdAsync(_repository, appId, cancellationToken);
        return gameId is not null &&
            await SetByGameIdAsync(userId, gameId.Value, favorite, cancellationToken);
    }

    private async Task ApplyAsync(int userId, long gameId, bool favorite, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetTrack<UserGameFavorite>()
            .FirstOrDefaultAsync(
                candidate => candidate.UserId == userId && candidate.GameId == gameId,
                cancellationToken);

        if (favorite)
        {
            // Ya marcado: nada que hacer, marcar dos veces no es un error.
            if (existing is null)
            {
                await _repository.Save(new UserGameFavorite { UserId = userId, GameId = gameId });
            }

            return;
        }

        // Desmarcar algo que no estaba marcado también es un no-op.
        if (existing is not null)
        {
            await _repository.RemoveAsync(existing);
        }
    }
}
