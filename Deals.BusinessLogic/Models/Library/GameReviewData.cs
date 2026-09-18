namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Validated input for creating a review. Months travel as <c>YYYY-MM</c>; the service stores day 1.
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
/// Input for updating a review. Identity (<c>gameId</c>, <c>platform</c>) is immutable, so it is absent.
/// </summary>
public sealed record GameReviewUpdate(
    string? StartedMonth,
    string? FinishedMonth,
    short? Score,
    bool IsGoty,
    string? Body);
