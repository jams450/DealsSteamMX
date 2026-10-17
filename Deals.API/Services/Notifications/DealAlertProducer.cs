using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Microsoft.Extensions.Options;

namespace Deals.API.Services.Notifications;

/// <summary>Counters of one producer phase, safe to log (no URLs, no tokens).</summary>
public sealed record DealAlertProduceReport(int Candidates, int Triggered, int Enqueued);

/// <summary>
/// Phase A of the job. Reads the fresh persisted snapshots, applies the pure policy and inserts the
/// firing decisions into the durable outbox. It never calls a provider and never sends anything.
///
/// Rejected candidates are dropped before <see cref="IDealAlertOutbox.TryEnqueueAsync"/> is called, so a
/// rejection cannot touch the outbox at all: the rows that already exist keep their status and attempts.
/// </summary>
public sealed class DealAlertProducer(
    IDealAlertCandidateReader reader,
    IDealAlertOutbox outbox,
    IOptions<NtfyOptions> options,
    TimeProvider clock)
{
    public async Task<DealAlertProduceReport> ProduceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var maxSnapshotAge = TimeSpan.FromHours(settings.MaxSnapshotAgeHours);

        var candidates = await reader.LoadFreshAsync(now, maxSnapshotAge, cancellationToken);
        var context = new DealAlertContext(now, maxSnapshotAge, settings.MinViableDiscountPercent);
        var decisions = DealAlertPolicy.EvaluateAll(candidates, context);
        var enqueued = await outbox.TryEnqueueAsync(decisions, now, cancellationToken);

        return new DealAlertProduceReport(candidates.Count, decisions.Count, enqueued);
    }
}
