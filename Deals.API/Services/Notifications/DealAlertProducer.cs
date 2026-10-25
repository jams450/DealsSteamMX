using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Microsoft.Extensions.Options;

namespace Deals.API.Services.Notifications;

/// <summary>Counters of one producer phase, safe to log (no URLs, no tokens).</summary>
public sealed record DealAlertProduceReport(int Candidates, int Triggered, int Enqueued);

/// <summary>
/// Phase A of the job. Reads the fresh persisted snapshots of the configured operator's interests, applies the
/// pure policy and inserts the firing decisions into the durable outbox. It never calls a provider and never
/// sends anything.
///
/// Rejected candidates are dropped before <see cref="IDealAlertOutbox.TryEnqueueAsync"/> is called, so a
/// rejection cannot touch the outbox at all: the rows that already exist keep their status and attempts.
///
/// The operator scope (<see cref="IDealAlertRecipientScope"/>) is a required dependency, not an optional one:
/// without it the type cannot be constructed, so there is no unscoped path that could produce global alerts. A
/// missing or inactive account produces nothing and does not even load a snapshot set. When an alert is sent is
/// the dispatcher's problem: it re-reads the same scope immediately before every send.
/// </summary>
public sealed class DealAlertProducer(
    IDealAlertCandidateReader reader,
    IDealAlertOutbox outbox,
    IOptions<NtfyOptions> options,
    TimeProvider clock,
    IDealAlertRecipientScope recipientScope)
{
    public async Task<DealAlertProduceReport> ProduceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var maxSnapshotAge = TimeSpan.FromHours(settings.MaxSnapshotAgeHours);

        // Fail closed before the expensive query. There is no unscoped path: without a valid operator this phase
        // produces nothing and never touches the outbox.
        var recipient = await recipientScope.LoadAsync(cancellationToken);
        if (recipient is null)
        {
            return new DealAlertProduceReport(0, 0, 0);
        }

        var fresh = await reader.LoadFreshForRecipientAsync(now, maxSnapshotAge, recipient, cancellationToken);

        // The scope is also applied in memory: a reader that returns more than asked for must still not be able
        // to enqueue somebody else's deal.
        var candidates = fresh
            .Where(candidate => recipient.Includes(candidate.GameId, candidate.SteamGameId))
            .ToList();
        var seen = fresh.Count;

        var context = new DealAlertContext(now, maxSnapshotAge, settings.MinViableDiscountPercent);
        var decisions = DealAlertPolicy.EvaluateAll(candidates, context);
        var enqueued = await outbox.TryEnqueueAsync(decisions, now, cancellationToken);

        return new DealAlertProduceReport(seen, decisions.Count, enqueued);
    }
}
