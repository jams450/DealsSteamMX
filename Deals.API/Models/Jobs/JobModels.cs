using System.Text.Json;

namespace Deals.API.Models.Jobs;

public sealed record JobRunSummaryResponse(
    int Total,
    int Running,
    int Ok,
    int Failed,
    DateTime? LastStartedAt);

public sealed record JobRunListItemResponse(
    long JobRunId,
    string Job,
    string Trigger,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    long? DurationMilliseconds,
    JsonElement? Details);

public sealed record JobRunHistoryResponse(
    JobRunSummaryResponse Summary,
    IReadOnlyList<JobRunListItemResponse> Items,
    int Page,
    int PageSize,
    int TotalPages);
