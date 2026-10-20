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

/// <summary>The applied filter travels back with the page, so the UI never guesses what it is showing.</summary>
public sealed record JobRunFilterResponse(string? Job, string? Status, string? Trigger);

/// <summary>
/// Run history. Additive to the previous contract: <c>summary</c>, <c>items</c>, <c>page</c>,
/// <c>pageSize</c> and <c>totalPages</c> keep their names and meaning, and <c>filters</c> says which
/// narrowing was applied.
/// </summary>
public sealed record JobRunHistoryResponse(
    JobRunSummaryResponse Summary,
    IReadOnlyList<JobRunListItemResponse> Items,
    int Page,
    int PageSize,
    int TotalPages,
    JobRunFilterResponse Filters);

public sealed record JobRunDetailResponse(
    long JobRunId,
    string Job,
    string Trigger,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    long? DurationMilliseconds,
    JsonElement? Details);

public sealed record JobOptionResponse(string Key, string Label, string Help, bool DefaultValue);

/// <summary>
/// One job as the operator may execute it. <c>enabled</c>/<c>disabledReason</c> report configuration
/// honestly, <c>destructive</c>/<c>requiresConfirmation</c> drive the confirmation step, and
/// <c>stale</c> marks a <c>running</c> row older than the abandoned window: the run never finished, so the
/// job is launchable again instead of blocked forever by a dead process.
/// </summary>
public sealed record JobCatalogItemResponse(
    string Job,
    string DisplayName,
    string Description,
    bool Enabled,
    string? DisabledReason,
    string Schedule,
    bool Running,
    bool Stale,
    bool Destructive,
    bool RequiresConfirmation,
    IReadOnlyList<JobOptionResponse> Options,
    JobRunListItemResponse? LastRun);

public sealed record JobCatalogResponse(IReadOnlyList<JobCatalogItemResponse> Items, DateTime GeneratedAt);

/// <summary>
/// Manual run request. <c>options</c> is validated against the job's bounded option set — never against
/// arbitrary configuration — and <c>confirm</c> is only meaningful for a destructive job.
/// </summary>
public sealed class JobRunRequest
{
    public JsonElement? Options { get; set; }

    public bool Confirm { get; set; }
}

/// <summary>Answer of an accepted request: the run id, so the page can follow that exact row.</summary>
public sealed record JobRunQueuedResponse(long JobRunId, string Job, string Status, DateTime RequestedAt);
