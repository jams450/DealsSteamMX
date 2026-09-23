using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Review CRUD with service-side validation (repo style: no CHECK constraints in SQL). The canonical
/// catalog is a hard dependency: a game with no <c>games</c> row cannot be reviewed. A game may have any
/// number of reviews per platform — a replay is a new review, not an edit of the old one. The score label
/// is computed on read by <see cref="ReviewScoreBands"/>, never persisted.
/// </summary>
public class ReviewService : IReviewService
{
    private const int MaxBodyLength = 4000;
    private const string MonthFormat = "yyyy-MM";

    private readonly IRepository _repository;

    public ReviewService(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<GameReview>> GetForGameAsync(
        int userId,
        long gameId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || gameId <= 0)
        {
            return [];
        }

        return await _repository.Get<GameReview>()
            .Where(review => review.UserId == userId && review.GameId == gameId)
            .OrderByDescending(review => review.FinishedMonth)
            .ThenByDescending(review => review.StartedMonth)
            .ThenByDescending(review => review.GameReviewId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GameReview>> GetForGamesAsync(
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

        return await _repository.Get<GameReview>()
            .Where(review => review.UserId == userId && ids.Contains(review.GameId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GameReview>> GetForSteamAppAsync(
        int userId,
        int appId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || appId <= 0)
        {
            return [];
        }

        // Same appid -> ('steam', appid) resolution the ownership block and the favorites use.
        var gameId = await SteamAppIdResolver.ResolveGameIdAsync(_repository, appId, cancellationToken);

        return gameId is null
            ? []
            : await GetForGameAsync(userId, gameId.Value, cancellationToken);
    }

    public async Task<GameReview> CreateAsync(
        int userId,
        GameReviewInput input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentException("La reseña es obligatoria", nameof(input));
        }

        var platform = ValidatePlatform(input.Platform);
        var status = ValidateStatus(input.Status);
        var score = ValidateScore(input.Score);
        var started = ParseMonth(input.StartedMonth, nameof(input.StartedMonth));
        var finished = ParseMonth(input.FinishedMonth, nameof(input.FinishedMonth));
        ValidateOrder(started, finished);
        var body = NormalizeBody(input.Body);

        var gameExists = await _repository.Get<Game>()
            .AnyAsync(game => game.GameId == input.GameId, cancellationToken);
        if (!gameExists)
        {
            throw new ArgumentException("El juego no existe en el catálogo", nameof(input.GameId));
        }

        // No duplicate check and no unique constraint: repeating a game is the point, so a second review
        // of the same (game, platform) is created, never merged into the first one.
        var review = new GameReview
        {
            UserId = userId,
            GameId = input.GameId,
            Platform = platform,
            StartedMonth = started,
            FinishedMonth = finished,
            Score = score,
            IsGoty = input.IsGoty,
            Status = status,
            Body = body
        };

        return await _repository.Save(review);
    }

    public async Task<GameReview?> UpdateAsync(
        int userId,
        long reviewId,
        GameReviewUpdate input,
        CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            throw new ArgumentException("La reseña es obligatoria", nameof(input));
        }

        var status = ValidateStatus(input.Status);
        var score = ValidateScore(input.Score);
        var started = ParseMonth(input.StartedMonth, nameof(input.StartedMonth));
        var finished = ParseMonth(input.FinishedMonth, nameof(input.FinishedMonth));
        ValidateOrder(started, finished);
        var body = NormalizeBody(input.Body);

        // Scoped by user: another user's review is "not found", never 403. Validation above is
        // payload-only, so a probe cannot infer whether the id exists.
        var review = await _repository.GetTrack<GameReview>()
            .FirstOrDefaultAsync(
                candidate => candidate.GameReviewId == reviewId && candidate.UserId == userId,
                cancellationToken);
        if (review is null)
        {
            return null;
        }

        review.StartedMonth = started;
        review.FinishedMonth = finished;
        review.Score = score;
        review.IsGoty = input.IsGoty;
        review.Status = status;
        review.Body = body;

        await _repository.SaveChangesAsync();
        return review;
    }

    public async Task<bool> DeleteAsync(
        int userId,
        long reviewId,
        CancellationToken cancellationToken = default)
    {
        var review = await _repository.GetTrack<GameReview>()
            .FirstOrDefaultAsync(
                candidate => candidate.GameReviewId == reviewId && candidate.UserId == userId,
                cancellationToken);
        if (review is null)
        {
            return false;
        }

        await _repository.RemoveAsync(review);
        return true;
    }

    private static string ValidatePlatform(string? platform)
    {
        var normalized = StoreKeys.Normalize(platform);
        if (normalized is null)
        {
            throw new ArgumentException("La plataforma no es una tienda conocida ni un slug de plataforma válido", nameof(platform));
        }

        return normalized;
    }

    // El estado es obligatorio: guardar una reseña sin saber si la partida se terminó o se abandonó deja el
    // filtro de estado (y el reporte por año) a medias. "Por jugar" no es un valor válido aquí: es la
    // ausencia de reseña.
    private static string ValidateStatus(string? status)
    {
        var trimmed = status?.Trim() ?? string.Empty;
        if (!ReviewStatuses.IsKnown(trimmed))
        {
            throw new ArgumentException(
                $"status debe ser {ReviewStatuses.Finished}, {ReviewStatuses.Completed} o {ReviewStatuses.Dropped}",
                nameof(status));
        }

        return trimmed;
    }

    private static short? ValidateScore(short? score)
    {
        if (score is < 0 or > 100)
        {
            throw new ArgumentException("score debe estar entre 0 y 100", nameof(score));
        }

        return score;
    }

    private static DateTime? ParseMonth(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                value.Trim(),
                MonthFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new ArgumentException($"{field} debe tener el formato YYYY-MM", field);
        }

        // Stored as day 1 of the month; DATE has no kind.
        return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
    }

    private static void ValidateOrder(DateTime? started, DateTime? finished)
    {
        if (started is not null && finished is not null && finished < started)
        {
            throw new ArgumentException("finishedMonth no puede ser anterior a startedMonth");
        }
    }

    private static string? NormalizeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        if (trimmed.Length > MaxBodyLength)
        {
            throw new ArgumentException($"body no puede superar los {MaxBodyLength} caracteres", nameof(body));
        }

        return trimmed;
    }
}
