using Deals.BusinessLogic.Models.Diagnostics;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Per-run collector of the safe sync diagnosis (spec S2/S3).
///
/// <para>
/// It is registered <c>scoped</c> on purpose and that is the whole isolation story: a job run owns one DI scope, so
/// its collector starts empty, ends with the run, and two runs can never add events to the same document. An
/// interactive request owns a different scope and therefore a different collector, which is never read — so a page
/// load cannot contaminate a job report, and a report cannot be widened by traffic outside the run.
/// </para>
///
/// <para>
/// Two counters, not one: every recorded event is counted in the aggregate summary (complete, whatever the solver
/// cap), while only the first <see cref="SyncDiagnosticsContract.MaxEvents"/> events keep their per-game detail. Both
/// numbers are reported, so a truncated list is never mistaken for the whole run.
/// </para>
/// </summary>
public interface ISyncDiagnosticsCollector
{
    /// <summary>
    /// True once a job run claimed this collector. Nothing is recorded (and no warning is logged) before that, which is
    /// what keeps an interactive request's traffic out of a job report: the request's own scope is never claimed.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Claims the collector for the current job run. Called by the two job executors and nowhere else, so the boundary
    /// between "a job is diagnosing" and "a page is being served" is one explicit call instead of a convention.
    /// </summary>
    void Activate();

    /// <summary>True once at least one event was recorded; a report is only attached when there is something to say.</summary>
    bool HasEvents { get; }

    /// <summary>
    /// Records one phase. Only closed tokens reach this method (they come from the enums), so the writer cannot
    /// invent a provider or a reason; the read side validates them again before rendering.
    /// </summary>
    void Record(
        int? appId,
        long? gameId,
        SyncDiagnosticProvider provider,
        SyncDiagnosticOutcome outcome,
        SyncDiagnosticReason reason,
        int? httpStatus = null,
        string? errorType = null,
        long? elapsedMs = null);


    /// <summary>
    /// Immutable document of the events recorded so far. Reading never clears the collector, so the same run can
    /// report on a failure path after having already built its result.
    /// </summary>
    SyncDiagnosticsSnapshot BuildSnapshot();
}
