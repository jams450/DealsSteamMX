using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces;

public interface IGameTierService
{
    Task<IReadOnlyList<GameTierEntry>> GetForYearAsync(int userId, int year, CancellationToken cancellationToken = default);
    Task<bool> SetAsync(int userId, int year, long gameId, string? tier, int position, CancellationToken cancellationToken = default);
}

public sealed record GameTierEntry(long GameId, string Title, string? ImageUrl, string Tier, int Position);
