using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Annual tier list keyed by reviewed play. A card is one <see cref="GameReview"/>, so a game played
/// twice — even twice on the same platform — ranks twice and each card saves independently (spec S5).
/// The year of a card is the review's <c>finished_month ?? started_month</c>, and it is the only year the
/// placement may be saved under: another user's review or a review dated elsewhere is a "not found", never
/// a write.
/// </summary>
public sealed class GameTierService(IRepository repository) : IGameTierService
{
    private const int UnrankedPosition = int.MaxValue;
    private static readonly string[] TierOrder = ["S", "A", "B", "C", "D", "F"];

    public async Task<IReadOnlyList<int>> GetYearsAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0) throw new ArgumentException("userId inválido");

        // Se agrupa por reseña y no por juego: un juego jugado en 2025 y otra vez en 2026 tiene que ofrecer
        // los dos años, y una reseña sin meses no pertenece a ninguno.
        var dates = await repository.Get<GameReview>()
            .Where(review => review.UserId == userId)
            .Select(review => review.FinishedMonth ?? review.StartedMonth)
            .ToListAsync(cancellationToken);

        return dates
            .Where(date => date.HasValue)
            .Select(date => date!.Value.Year)
            .Distinct()
            .OrderByDescending(year => year)
            .ToList();
    }

    public async Task<IReadOnlyList<GameTierEntry>> GetForYearAsync(int userId, int year, CancellationToken cancellationToken = default)
    {
        ValidateScope(userId, year);

        var reviews = await repository.Get<GameReview>()
            .Where(review => review.UserId == userId &&
                ((review.FinishedMonth ?? review.StartedMonth) != null) &&
                (review.FinishedMonth ?? review.StartedMonth)!.Value.Year == year)
            .Select(review => new { review.GameReviewId, review.GameId, review.Platform, review.IsGoty })
            .ToListAsync(cancellationToken);

        if (reviews.Count == 0) return [];

        var gameIds = reviews.Select(review => review.GameId).Distinct().ToList();
        var games = await repository.Get<Game>()
            .Where(game => gameIds.Contains(game.GameId))
            .Select(game => new { game.GameId, game.Title, game.ImageUrl })
            .ToListAsync(cancellationToken);
        var gameById = games.ToDictionary(game => game.GameId);

        // Los puestos se leen por (user, year) y se cruzan por reviewId: un puesto de una reseña que ya no
        // cae en este año (la reseña se editó después) no se muestra ni se reutiliza.
        var placements = await repository.Get<UserGameTier>()
            .Where(placement => placement.UserId == userId && placement.Year == year)
            .Select(placement => new { placement.GameReviewId, placement.Tier, placement.Position })
            .ToListAsync(cancellationToken);
        var placementByReview = placements
            .GroupBy(placement => placement.GameReviewId)
            .ToDictionary(group => group.Key, group => group.First());

        var cards = new List<GameTierEntry>(reviews.Count);
        foreach (var review in reviews)
        {
            // El catálogo es FK obligatoria de la reseña; si faltara la fila no hay tarjeta que pintar, y
            // inventar un título rompería el contrato del cliente (una lista inválida se descarta entera).
            if (!gameById.TryGetValue(review.GameId, out var game)) continue;

            var tier = "Unranked";
            var position = UnrankedPosition;
            if (placementByReview.TryGetValue(review.GameReviewId, out var placement) &&
                (placement.Tier == "Unranked" || Array.IndexOf(TierOrder, placement.Tier) >= 0))
            {
                tier = placement.Tier;
                position = placement.Position;
            }

            cards.Add(new GameTierEntry(
                review.GameReviewId,
                review.GameId,
                game.Title,
                game.ImageUrl,
                review.Platform,
                review.IsGoty,
                tier,
                position));
        }

        return cards
            .OrderBy(card => card.Tier == "Unranked" ? 1 : 0)
            .ThenBy(card => Array.IndexOf(TierOrder, card.Tier))
            .ThenBy(card => card.Position)
            .ThenBy(card => card.Title, StringComparer.Ordinal)
            .ThenBy(card => card.ReviewId)
            .ToList();
    }

    public async Task<bool> SetAsync(int userId, int year, long reviewId, string? tier, int position, CancellationToken cancellationToken = default)
    {
        ValidateScope(userId, year);
        var normalizedTier = tier?.Trim().ToUpperInvariant();
        if (reviewId <= 0 || normalizedTier is null || Array.IndexOf(TierOrder, normalizedTier) < 0 || position < 0 || position > 10000)
            throw new ArgumentException("reviewId, tier S–F y position (0–10000) son obligatorios");

        // Scoped by user: la reseña de otra persona es "no encontrada", nunca 403, así que una sonda no
        // puede deducir si el id existe.
        var review = await repository.Get<GameReview>()
            .Where(candidate => candidate.GameReviewId == reviewId && candidate.UserId == userId)
            .Select(candidate => new { candidate.GameId, candidate.FinishedMonth, candidate.StartedMonth })
            .FirstOrDefaultAsync(cancellationToken);
        if (review is null) return false;

        // El año del puesto no se acepta del cliente: es el que la reseña declara. Un desacuerdo es un
        // "no encontrado" y no escribe nada.
        if ((review.FinishedMonth ?? review.StartedMonth)?.Year != year) return false;

        var current = await repository.GetTrack<UserGameTier>().FirstOrDefaultAsync(placement =>
            placement.UserId == userId && placement.Year == year && placement.GameReviewId == reviewId, cancellationToken);
        if (current is null)
        {
            await repository.Save(new UserGameTier
            {
                UserId = userId,
                Year = year,
                GameReviewId = reviewId,
                GameId = review.GameId,
                Tier = normalizedTier,
                Position = position
            });
        }
        else
        {
            current.GameId = review.GameId;
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
