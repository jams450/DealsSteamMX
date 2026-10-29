using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces;

public interface IGameTierService
{
    /// <summary>Years the caller can pick, derived per review from its finished (else started) month, newest first.</summary>
    Task<IReadOnlyList<int>> GetYearsAsync(int userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameTierEntry>> GetForYearAsync(int userId, int year, CancellationToken cancellationToken = default);

    /// <summary>
    /// Places one reviewed play. Returns <c>false</c> when the review is not the caller's or is dated in
    /// another year; nothing is written in that case.
    /// </summary>
    Task<bool> SetAsync(int userId, int year, long reviewId, string? tier, int position, CancellationToken cancellationToken = default);
}

/// <summary>
/// One tier card: a reviewed play, not a game. <see cref="ReviewId"/> is the identity the client saves,
/// drags and sorts by; <see cref="GameId"/> only anchors the canonical game for merges.
/// </summary>
public sealed record GameTierEntry(
    long ReviewId,
    long GameId,
    string Title,
    string? ImageUrl,
    string Platform,
    bool IsGoty,
    string Tier,
    int Position);
