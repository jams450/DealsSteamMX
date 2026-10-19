using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

public sealed class GameTierService(IRepository repository) : IGameTierService
{
    private static readonly HashSet<string> Tiers = ["S", "A", "B", "C", "D", "F"];

    public async Task<IReadOnlyList<GameTierEntry>> GetForYearAsync(int userId, int year, CancellationToken cancellationToken = default)
    {
        ValidateScope(userId, year);
        var reviewed = await repository.Get<GameReview>()
            .Where(review => review.UserId == userId &&
                ((review.FinishedMonth ?? review.StartedMonth) != null) &&
                (review.FinishedMonth ?? review.StartedMonth)!.Value.Year == year)
            .GroupBy(review => review.GameId)
            .Select(group => group.Key)
            .ToListAsync(cancellationToken);

        if (reviewed.Count == 0) return [];

        var games = await repository.Get<Game>()
            .Where(game => reviewed.Contains(game.GameId))
            .Select(game => new { game.GameId, game.Title, game.ImageUrl })
            .ToListAsync(cancellationToken);
        var placements = await repository.Get<UserGameTier>()
            .Where(entry => entry.UserId == userId && entry.Year == year && reviewed.Contains(entry.GameId))
            .ToListAsync(cancellationToken);
        var placementById = placements.ToDictionary(entry => entry.GameId);

        return games.Select(game => placementById.TryGetValue(game.GameId, out var placement)
                ? new GameTierEntry(game.GameId, game.Title, game.ImageUrl, placement.Tier, placement.Position)
                : new GameTierEntry(game.GameId, game.Title, game.ImageUrl, "Unranked", int.MaxValue))
            .OrderBy(entry => entry.Tier == "Unranked" ? 1 : 0)
            .ThenBy(entry => Array.IndexOf(new[] { "S", "A", "B", "C", "D", "F" }, entry.Tier))
            .ThenBy(entry => entry.Position)
            .ThenBy(entry => entry.Title)
            .ToList();
    }

    public async Task<bool> SetAsync(int userId, int year, long gameId, string? tier, int position, CancellationToken cancellationToken = default)
    {
        ValidateScope(userId, year);
        var normalizedTier = tier?.Trim().ToUpperInvariant();
        if (gameId <= 0 || normalizedTier is null || !Tiers.Contains(normalizedTier) || position < 0 || position > 10000)
            throw new ArgumentException("gameId, tier S–F y position (0–10000) son obligatorios");

        var hasReview = await repository.Get<GameReview>().AnyAsync(review => review.UserId == userId && review.GameId == gameId &&
            ((review.FinishedMonth ?? review.StartedMonth) != null) &&
            (review.FinishedMonth ?? review.StartedMonth)!.Value.Year == year, cancellationToken);
        if (!hasReview) return false;

        var current = await repository.GetTrack<UserGameTier>().FirstOrDefaultAsync(entry =>
            entry.UserId == userId && entry.Year == year && entry.GameId == gameId, cancellationToken);
        if (current is null)
            await repository.Save(new UserGameTier { UserId = userId, Year = year, GameId = gameId, Tier = normalizedTier, Position = position });
        else
        {
            current.Tier = normalizedTier;
            current.Position = position;
            await repository.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private static void ValidateScope(int userId, int year)
    {
        if (userId <= 0 || year < 1000 || year > 9999) throw new ArgumentException("userId o year inválido");
    }
}
