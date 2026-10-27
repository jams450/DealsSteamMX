using System.Diagnostics;
using Deals.BusinessLogic.Models.Diagnostics;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Everything one provider phase observed about itself while it ran: how long it took, and, when it ended without a
/// price, why. Created by <see cref="SteamGameService"/> per phase and never shared, so nothing here can leak from
/// one game, one run or one request into another.
///
/// <para>
/// It exists because the pipeline absorbs a provider fault into a <c>Failed</c> phase result inside the fetcher: by
/// the time the phase returns, the exception is gone. Without this carrier the report could only say "failed" and
/// the 429/403/timeout/parse distinction — the whole point of the diagnosis — would be lost.
/// </para>
/// </summary>
public sealed class SyncPhaseProbe
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    /// <summary>
    /// Why the phase ended without refreshing: an authoritative no-match reason, or the gate that skipped it. Set by
    /// the phase itself, which is the only place that knows.
    /// </summary>
    public SyncDiagnosticReason? Note { get; private set; }

    /// <summary>The exception that ended the phase, kept only to classify it — it is never persisted or rendered.</summary>
    public Exception? Cause { get; private set; }

    /// <summary>
    /// The outcome this phase claims, when it is not the one its typed result implies. A non-comparable item is the
    /// case: the phase returns "no match" (nothing was found) but the provider was never asked, which is a skip.
    /// </summary>
    public SyncDiagnosticOutcome? Outcome { get; private set; }

    /// <summary>Records the reason of an answer that is not a failure (no identity, no offer, gate, ...).</summary>
    public void Explain(SyncDiagnosticReason reason) => Note = reason;

    /// <summary>Records a phase the provider was never asked to answer, and why.</summary>
    public void Skip(SyncDiagnosticReason reason)
    {
        Note = reason;
        Outcome = SyncDiagnosticOutcome.Skipped;
    }

    /// <summary>Records the fault the phase absorbed so the report can name it.</summary>
    public void Capture(Exception exception) => Cause = exception;
}
