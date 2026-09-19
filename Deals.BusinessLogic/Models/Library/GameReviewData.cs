namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Validated input for creating a review. Months travel as <c>YYYY-MM</c>; the service stores day 1. Adding
/// a review never replaces an existing one on the same (<c>GameId</c>, <c>Platform</c>).
/// </summary>
public sealed record GameReviewInput(
    long GameId,
    string? Platform,
    string? StartedMonth,
    string? FinishedMonth,
    short? Score,
    bool IsGoty,
    string? Body);

/// <summary>
/// Input for updating one review. Where the review lives (<c>gameId</c>, <c>platform</c>) is immutable, so it
/// is absent: only the review named by id changes.
/// </summary>
public sealed record GameReviewUpdate(
    string? StartedMonth,
    string? FinishedMonth,
    short? Score,
    bool IsGoty,
    string? Body);
