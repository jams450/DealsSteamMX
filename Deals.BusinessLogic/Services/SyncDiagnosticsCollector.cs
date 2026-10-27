using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Per-run collector of the safe sync diagnosis. Registered scoped, so one run owns one instance: no static state,
/// no shared list, and therefore no way for a later run (or an interactive request in its own scope) to add events to
/// a report it did not produce.
///
/// <para>
/// Two counters, not one, and that is the whole reason this class is not a list: every event increments the
/// aggregate of its (provider, outcome, reason), while only the first
/// <see cref="SyncDiagnosticsContract.MaxEvents"/> events keep their per-game detail. A report assembled from the
/// bounded slice would claim 200 failures after 600 games; this one claims 600 and shows 200.
/// </para>
///
/// <para>
/// The aggregate is bounded by construction instead of by a cut-off: its key is (provider, outcome, reason) over closed
/// vocabularies — 7 × 5 × 13 = 455 lines at worst, under the page's 512 — and the status is deliberately not part of the
/// key (a run can see hundreds of distinct statuses and the operator asked for the reason breakdown). The actual status
/// stays on every detailed event, so <c>summary[].httpStatus</c> is always null and means "this line is not about a
/// status", never "the status was unavailable". Neither a cap nor an overflow bucket is needed, so no count is ever
/// dropped or folded. The per-game slice is the only thing bounded by
/// <see cref="SyncDiagnosticsContract.MaxEvents"/>.
/// </para>
///
/// <para>
/// Thread safety is not decoration: the gg.deals, Epic and Microsoft phases of one game run concurrently
/// (<c>Task.WhenAll</c>), so three phases can record at the same moment. The lock is over both the aggregate and the
/// slice, and nothing inside it calls a provider, the database or the logger's formatting of untrusted text.
/// </para>
///
/// <para>
/// A failure is announced once per distinct diagnosis — the first time a (provider, outcome, reason) combination
/// appears in the run — with only the safe fields: provider, app id, outcome, reason, the actual status of that first
/// occurrence, exception type and elapsed time. The exception object is never passed to the logger from
/// here, so a provider message, URL, payload or stack cannot ride along with it, and a cycle with thousands of
/// failures does not turn into thousands of warning lines: the first one says what happened and the summary counts
/// the rest.
/// </para>
/// </summary>
public sealed class SyncDiagnosticsCollector(ILogger<SyncDiagnosticsCollector>? logger = null) : ISyncDiagnosticsCollector
{
    private readonly object _gate = new();
    private readonly List<SyncDiagnosticEvent> _events = [];
    private readonly List<SyncDiagnosticSummaryRow> _summary = [];
    private readonly Dictionary<SummaryKey, int> _summaryIndex = [];
    private int _total;
    private bool _active;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public void Activate()
    {
        lock (_gate)
        {
            _active = true;
        }
    }

    public bool HasEvents
    {
        get
        {
            lock (_gate)
            {
                return _total > 0;
            }
        }
    }

    public void Record(
        int? appId,
        long? gameId,
        SyncDiagnosticProvider provider,
        SyncDiagnosticOutcome outcome,
        SyncDiagnosticReason reason,
        int? httpStatus = null,
        string? errorType = null,
        long? elapsedMs = null)
    {
        // Default inactive: a page load shares the DI scope with nothing that claims the collector, so an
        // interactive request can neither record an event nor emit a warning. Only the two job executors call
        // Activate (spec S2).
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }
        }

        var diagnosticEvent = new SyncDiagnosticEvent(
            // A non-positive app id is not a game; it is dropped rather than persisted as if it were.
            appId is > 0 ? appId : null,
            gameId is > 0 ? gameId : null,
            provider.Token(),
            outcome.Token(),
            reason.Token(),
            // Only a status an HTTP response can really have.
            httpStatus is >= 100 and <= 599 ? httpStatus : null,
            SyncDiagnosticsContract.SafeErrorType(errorType),
            elapsedMs is >= 0 ? elapsedMs : null);

        var firstOfItsKind = false;
        lock (_gate)
        {
            _total++;

            // Grouped by reason, not by status (parent decision): 7 providers × 5 outcomes × 13 reasons = 455 lines
            // worst case, under the page's ceiling, and the operator asked "how many per reason". The actual status
            // is not lost — it stays on the detailed event — and a null here therefore means "this aggregate is not
            // about a status", never "the status was unavailable".
            var key = new SummaryKey(diagnosticEvent.Provider, diagnosticEvent.Outcome, diagnosticEvent.Reason);
            if (_summaryIndex.TryGetValue(key, out var index))
            {
                _summary[index] = _summary[index] with { Count = _summary[index].Count + 1 };
            }
            else
            {
                firstOfItsKind = true;
                _summaryIndex[key] = _summary.Count;
                _summary.Add(new SyncDiagnosticSummaryRow(
                    key.Provider, key.Outcome, key.Reason, HttpStatus: null, Count: 1));
            }

            // The cap is on detail, never on counting: the summary above already includes this event.
            if (_events.Count < SyncDiagnosticsContract.MaxEvents)
            {
                _events.Add(diagnosticEvent);
            }
        }

        // One safe line per distinct diagnosis of the run (provider, outcome, reason). The actual status of the first
        // occurrence travels with the warning, so the log keeps the 403/429 detail while the summary stays bounded.
        if (firstOfItsKind && outcome is SyncDiagnosticOutcome.Failed or SyncDiagnosticOutcome.Cancelled)
        {
            logger?.LogWarning(
                "[sync.diagnostics] provider={Provider} appId={AppId} outcome={Outcome} reason={Reason} httpStatus={HttpStatus} errorType={ErrorType} elapsedMs={ElapsedMs}",
                diagnosticEvent.Provider,
                diagnosticEvent.AppId,
                diagnosticEvent.Outcome,
                diagnosticEvent.Reason,
                diagnosticEvent.HttpStatus,
                diagnosticEvent.ErrorType,
                diagnosticEvent.ElapsedMs);
        }
    }

    public SyncDiagnosticsSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            var shown = _events.Count;
            return new SyncDiagnosticsSnapshot(
                SyncDiagnosticsContract.SchemaVersion,
                _total,
                shown,
                // Never negative: the total can be smaller than the slice only if a caller bypassed Record.
                Math.Max(0, _total - shown),
                _summary.ToArray(),
                _events.ToArray());
        }
    }

    /// <summary>
    /// Identity of one aggregate line: the closed (provider, outcome, reason) triple. The status is deliberately not
    /// part of it — see the note where the key is built.
    /// </summary>
    private readonly record struct SummaryKey(string Provider, string Outcome, string Reason);
}
