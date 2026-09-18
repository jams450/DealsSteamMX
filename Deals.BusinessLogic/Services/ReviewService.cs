using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Review CRUD with service-side validation (repo style: no CHECK constraints in SQL). The canonical
/// catalog is a hard dependency: a game with no <c>games</c> row cannot be reviewed. The score label is
/// computed on read by <see cref="ReviewScoreBands"/>, never persisted.
/// </summary>
public class ReviewService : IReviewService
{
    private const int MaxBodyLength = 4000;
    private const string MonthFormat = "yyyy-MM";
    private const string SteamNamespace = "steam";
    private const string DuplicateMessage = "Ya existe una reseña para ese juego en esa plataforma";

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
            .OrderBy(review => review.Platform)
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

        var appIdText = appId.ToString(CultureInfo.InvariantCulture);

        // Same appid -> ('steam', appid) resolution the ownership block uses.
        var gameId = await (
                from externalId in _repository.Get<GameExternalId>()
                where externalId.NamespaceName == SteamNamespace && externalId.ExternalId == appIdText
                select (long?)externalId.GameId)
            .FirstOrDefaultAsync(cancellationToken);

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

        var duplicate = await _repository.Get<GameReview>()
            .AnyAsync(
                review => review.UserId == userId &&
                    review.GameId == input.GameId &&
                    review.Platform == platform,
                cancellationToken);
        if (duplicate)
        {
            throw new ArgumentException(DuplicateMessage, nameof(input));
        }

        var review = new GameReview
        {
            UserId = userId,
            GameId = input.GameId,
            Platform = platform,
            StartedMonth = started,
            FinishedMonth = finished,
            Score = score,
            IsGoty = input.IsGoty,
            Body = body
        };

        try
        {
            return await _repository.Save(review);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The unique constraint is the backstop for a race between the check above and the insert.
            throw new ArgumentException(DuplicateMessage, nameof(input));
        }
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
        var trimmed = platform?.Trim() ?? string.Empty;
        if (!StoreKeys.IsKnown(trimmed))
        {
            throw new ArgumentException("La plataforma no es una tienda conocida", nameof(platform));
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

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };
}
