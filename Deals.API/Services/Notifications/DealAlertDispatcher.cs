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
/// Two operator gates run before any send:
/// <list type="number">
/// <item>an initial read of the operator scope (<see cref="IDealAlertRecipientScope"/>). With no valid operator
/// the dispatcher claims nothing, so the pending backlog is preserved for the moment the operator is configured
/// or reactivated instead of being burned as stale;</item>
/// <item>a second read of the same scope <b>for every claimed row</b>, immediately after the offer revalidation
/// and immediately before <see cref="INtfyAlertSender.SendAsync"/>. The first read is only a snapshot: an
/// interest removed, an account deactivated or the operator switched while the cycle runs is retired as stale
/// and never pushed. The re-read must answer with the same account id the row was claimed for.</item>
/// </list>
///
/// Stale revalidation is the third gate: an alert is only sent while the offer still exists and still fires the
/// same dedup identity. If the price moved or the offer vanished, the row is retired as stale instead of pushing
/// a deal that is no longer there.
///
/// Sent rows are never touched: they are not pending, so they are never claimed and switching the operator does
/// not replay a previously sent same-price alert (documented limitation, see docs/NTFY_DEAL_ALERTS.md).
/// </summary>
public sealed class DealAlertDispatcher(
    IDealAlertOutbox outbox,
    IDealAlertCandidateSource source,
    INtfyAlertSender sender,
    IOptions<NtfyOptions> options,
    TimeProvider clock,
    ILogger<DealAlertDispatcher> logger,
    IDealAlertRecipientScope recipientScope)
{
    public async Task<DealAlertDispatchReport> DispatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;

        // Initial gate: no valid operator means nothing is claimed, so the pending backlog is preserved for
        // the moment the account exists or is active again instead of being burned as stale.
        var recipient = await recipientScope.LoadAsync(cancellationToken);
        if (recipient is null)
        {
            logger.LogWarning("[ntfy] dispatch skipped: no active notification operator configured");
            return new DealAlertDispatchReport(0, 0, 0, 0, 0);
        }

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

            // Last-moment operator recheck, after revalidation and immediately before the send: the cycle's
            // first read is only a snapshot. A game unfavorited, a wishlist row removed, the account deactivated
            // or the configured operator switched while this cycle runs must never receive this row.
            if (!await StillCurrentOperatorAsync(recipient, alert, cancellationToken))
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

    /// <summary>
    /// Re-reads the operator scope for one claimed row. The account id must be the same one the row was claimed
    /// for, and the row identity must still be inside that account's interests; anything else retires the row
    /// instead of sending it.
    /// </summary>
    private async Task<bool> StillCurrentOperatorAsync(
        DealAlertRecipient claimedFor,
        DealAlertEvent alert,
        CancellationToken cancellationToken)
    {
        var current = await recipientScope.LoadAsync(cancellationToken);
        return current is not null &&
            current.UserId == claimedFor.UserId &&
            current.Includes(alert.GameId, alert.SteamGameId);
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
