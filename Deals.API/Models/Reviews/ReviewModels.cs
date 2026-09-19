using System.Globalization;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;

namespace Deals.API.Models.Reviews;

/// <summary>
/// Wire contract of a review. Months are <c>YYYY-MM</c> strings and <see cref="ScoreLabel"/> is computed
/// by <see cref="ReviewScoreBands"/>, never persisted. Frozen: the frontend codes against this exactly.
/// </summary>
public sealed record ReviewResponse(
    long ReviewId,
    long GameId,
    string Platform,
    string? StartedMonth,
    string? FinishedMonth,
    short? Score,
    string? ScoreLabel,
    bool IsGoty,
    string? Body,
    DateTime Created,
    DateTime? Updated)
{
    public static ReviewResponse From(GameReview review) => new(
        review.GameReviewId,
        review.GameId,
        review.Platform,
        ToMonth(review.StartedMonth),
        ToMonth(review.FinishedMonth),
        review.Score,
        ReviewScoreBands.Label(review.Score),
        review.IsGoty,
        review.Body,
        review.Created ?? DateTime.UtcNow,
        review.Updated);

    private static string? ToMonth(DateTime? value) =>
        value?.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}

/// <summary>
/// Create payload. The (<see cref="GameId"/>, <see cref="Platform"/>) pair says where the review lands;
/// it is not an identity: the same pair can already have reviews and this one is added next to them.
/// </summary>
public sealed record ReviewCreateRequest(
    long GameId,
    string? Platform,
    string? StartedMonth,
    string? FinishedMonth,
    short? Score,
    bool IsGoty,
    string? Body);

/// <summary>Update payload: where the review lives is immutable and therefore absent.</summary>
public sealed record ReviewUpdateRequest(
    string? StartedMonth,
    string? FinishedMonth,
    short? Score,
    bool IsGoty,
    string? Body);
