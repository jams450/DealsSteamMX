using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.Services.Notifications;

/// <summary>Counters of one dispatcher phase, safe to log (no URLs, no tokens).</summary>
public sealed record DealAlertDispatchReport(int Claimed, int Sent, int Retried, int Failed, int Stale);

/// <summary>
/// Phase B of the job. Claims due outbox rows, revalidates each against the live snapshot, delivers it and
/// records the outcome.
///
/// Stale revalidation is the safety valve: an alert is only sent while the offer still exists and still
/// fires the same dedup identity. If the price moved or the offer vanished, the row is retired as stale
/// instead of pushing a deal that is no longer there.
/// </summary>
public sealed class DealAlertDispatcher(
    IDealAlertOutbox outbox,
    IDealAlertCandidateSource source,
    INtfyAlertSender sender,
    IOptions<NtfyOptions> options,
    TimeProvider clock,
    ILogger<DealAlertDispatcher> logger)
{
    public async Task<DealAlertDispatchReport> DispatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await outbox.ClaimDueAsync(settings.DispatchBatchSize, now, cancellationToken);

        var sent = 0;
        var retried = 0;
        var failed = 0;
        var stale = 0;

        for (var index = 0; index < due.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alert = due[index];

            if (!await StillValidAsync(alert, settings, now, cancellationToken))
            {
                await outbox.MarkStaleAsync(alert, cancellationToken);
                stale++;
                continue;
            }

            var message = new DealAlertMessage(alert.Title, alert.Message, alert.ClickUrl);
            var outcome = await sender.SendAsync(message, cancellationToken);

            switch (outcome)
            {
                case NtfySendOutcome.Delivered:
                    await outbox.MarkSentAsync(alert, now, cancellationToken);
                    sent++;
                    break;
                case NtfySendOutcome.Permanent:
                    await outbox.MarkFailedAsync(alert, nameof(NtfySendOutcome.Permanent), cancellationToken);
                    failed++;
                    break;
                default:
                    // Retryable or Ambiguous. Ambiguous counts as retryable on purpose: the message may
                    // already be out, so the channel is at-least-once.
                    if (alert.Attempts + 1 >= settings.MaxAttempts)
                    {
                        await outbox.MarkFailedAsync(alert, outcome.ToString(), cancellationToken);
                        failed++;
                    }
                    else
                    {
                        var delay = NtfyRetrySchedule.NextDelay(
                            alert.Attempts + 1,
                            TimeSpan.FromSeconds(settings.RetryBaseSeconds),
                            TimeSpan.FromSeconds(settings.RetryMaxSeconds));
                        await outbox.MarkRetryAsync(alert, outcome.ToString(), now + delay, cancellationToken);
                        retried++;
                    }
                    break;
            }

            if (index < due.Count - 1 && settings.MinSecondsBetweenSends > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.MinSecondsBetweenSends), cancellationToken);
            }
        }

        if (due.Count > 0)
        {
            logger.LogInformation(
                "[ntfy] dispatch claimed={Claimed} sent={Sent} retried={Retried} failed={Failed} stale={Stale}",
                due.Count,
                sent,
                retried,
                failed,
                stale);
        }

        return new DealAlertDispatchReport(due.Count, sent, retried, failed, stale);
    }

    private async Task<bool> StillValidAsync(
        DealAlertEvent alert,
        NtfyOptions settings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var current = await source.FindCurrentAsync(alert, cancellationToken);
        if (current is null)
        {
            return false;
        }

        var context = new DealAlertContext(now, TimeSpan.FromHours(settings.MaxSnapshotAgeHours), settings.MinViableDiscountPercent);
        var decision = DealAlertPolicy.Evaluate(current, context);
        return decision.Triggered &&
            string.Equals(decision.DedupKey, alert.DedupKey, StringComparison.Ordinal);
    }
}
