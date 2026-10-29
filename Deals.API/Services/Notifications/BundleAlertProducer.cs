using Microsoft.Extensions.Options;

namespace Deals.API.Services.Notifications;

/// <summary>Counters of one bundle producer phase, safe to log (no URLs, no tokens, no names).</summary>
public sealed record BundleAlertProduceReport(int Candidates, int Triggered, int Enqueued, int Suppressed);

/// <summary>
/// Phase A of the bundle reminders (spec S2). It reads the active bundles whose persisted expiry is inside the
/// red window, drops the ones the operator already acquired and inserts one reminder per
/// (operator, source family, bundle key, expiry) into the bundle outbox. It never calls a provider and never
/// sends anything.
///
/// <para>
/// <b>No interest gate.</b> The user asked for "todos los bundles pendientes", so favorites and the wishlist are
/// not consulted here — that is the one deliberate difference from <see cref="DealAlertProducer"/>, whose
/// interest-scoped gate is untouched. The only account that exists for this family is the configured
/// notification operator (<see cref="NtfyOptions.UserId"/>), which is not inferred from a caller and is
/// validated through the same <see cref="IDealAlertRecipientScope"/> the offer alerts use: no operator, an
/// unknown operator or an inactive operator produces nothing at all. That is also why the dedup key carries the
/// operator id — switching accounts never replays another account's reminders.
/// </para>
///
/// <para>
/// The window is applied twice: in SQL (bounded read) and again in memory here, so a source that returns
/// something out of the window cannot queue a reminder. The <see cref="BundleAlertPolicy.IsWithinAlertWindow"/>
/// rule is the same one the badge uses, which is what keeps "red" from meaning two different things in the UI
/// and in the notification.
/// </para>
/// </summary>
public sealed class BundleAlertProducer(
    IBundleAlertSource source,
    IBundleAlertOutbox outbox,
    IBundlePurchaseGate purchases,
    IOptions<NtfyOptions> options,
    TimeProvider clock,
    IDealAlertRecipientScope recipientScope)
{
    public async Task<BundleAlertProduceReport> ProduceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        // Fail closed before any read. The job also refuses to run when the notifier is disabled; this second
        // gate keeps a manually constructed pipeline (a check, an admin path) from queueing while it is off.
        if (!settings.Enabled)
        {
            return new BundleAlertProduceReport(0, 0, 0, 0);
        }

        var now = clock.GetUtcNow().UtcDateTime;

        var recipient = await recipientScope.LoadAsync(cancellationToken);
        if (recipient is null)
        {
            return new BundleAlertProduceReport(0, 0, 0, 0);
        }

        var pending = await source.LoadPendingAsync(now, cancellationToken);

        var decisions = new List<BundleAlertDecision>(pending.Count);
        var suppressed = 0;
        foreach (var candidate in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Defence in depth: the SQL source already restricts the window, and this is the rule itself.
            if (!BundleAlertPolicy.IsWithinAlertWindow(candidate.ExpiresAt, now))
            {
                continue;
            }

            if (await purchases.IsAcquiredAsync(recipient.UserId, candidate.Source, candidate.BundleKey, cancellationToken))
            {
                suppressed++;
                continue;
            }

            decisions.Add(new BundleAlertDecision(
                recipient.UserId,
                candidate,
                BundleAlertPolicy.DedupKey(recipient.UserId, candidate.Source, candidate.BundleKey, candidate.ExpiresAt),
                BundleAlertPolicy.BuildMessage(candidate, now)));
        }

        var enqueued = await outbox.TryEnqueueAsync(decisions, now, cancellationToken);

        return new BundleAlertProduceReport(pending.Count, decisions.Count, enqueued, suppressed);
    }
}
