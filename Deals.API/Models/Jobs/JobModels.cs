using System.Text.Json;
using System.Text.Json.Serialization;
using Deals.API.Services.Jobs;

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

/// <summary>
/// One option as the admin page reads it. The shape is additive to the previous contract (spec S5): the four fields
/// keep their names, <see cref="DefaultValue"/> is a boolean for a switch and a number for an integer option, and
/// <see cref="Type"/>/<see cref="MinValue"/>/<see cref="MaxValue"/> are new. The two bounds are null (and therefore
/// omitted) for a switch, so an old-style option still serialises as it did plus its explicit type.
/// </summary>
public sealed record JobOptionResponse(
    string Key,
    string Label,
    string Help,
    object DefaultValue,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MinValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxValue = null)
{
    /// <summary>
    /// Maps a catalog descriptor to the response the admin page reads. Kept here, next to the wire record, so the
    /// option contract is tested through the same mapping the controller uses instead of a copy of it.
    /// </summary>
    public static JobOptionResponse From(JobOptionDescriptor option) =>
        new(option.Key, option.Label, option.Help, option.DefaultValue, option.Type, option.MinValue, option.MaxValue);
}

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
