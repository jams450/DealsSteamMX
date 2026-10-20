using System.Text.Json;

namespace Deals.BusinessLogic.Models.Jobs;

/// <summary>
/// Filter of the administrative run history. Every field is optional: the UI narrows the same history the
/// API has always returned, so an empty filter is the previous behaviour. Strings are matched exactly
/// (ordinal), because they are stable machine keys written by the jobs themselves, not free text.
/// </summary>
public sealed record JobRunQueryFilter(
    string? Job = null,
    string? Status = null,
    string? Trigger = null,
    int Page = 1,
    int PageSize = 25);

public sealed record JobRunQuerySummary(int Total, int Running, int Ok, int Failed, DateTime? LastStartedAt);

public sealed record JobRunQueryItem(
    long JobRunId,
    string Job,
    string Trigger,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    long? DurationMilliseconds,
    JsonElement? Details);

public sealed record JobRunQueryPage(
    JobRunQuerySummary Summary,
    IReadOnlyList<JobRunQueryItem> Items,
    int Page,
    int PageSize,
    int TotalPages,
    JobRunQueryFilter Filter);

/// <summary>
/// The newest run of one job, plus whether it is still open. The catalog needs both together: a job whose
/// latest row is <c>running</c> is the one the operator must not launch again.
/// </summary>
public sealed record JobRunLatest(
    string Job,
    long JobRunId,
    string Trigger,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    long? DurationMilliseconds,
    JsonElement? Details);
