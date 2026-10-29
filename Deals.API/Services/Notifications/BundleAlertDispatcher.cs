using Deals.BusinessLogic.Models.Notifications;
using Deals.Models.Entities;
using Microsoft.Extensions.Options;

namespace Deals.API.Services.Notifications;

/// <summary>Counters of one bundle dispatcher phase, safe to log (no URLs, no tokens, no names).</summary>
public sealed record BundleAlertDispatchReport(
    int Claimed,
    int Sent,
    int Retried,
    int Failed,
    int Stale,
    int Suppressed);

/// <summary>
/// Phase B of the bundle reminders. Claims due outbox rows and, for each one, runs every gate before the HTTP
/// call:
/// <list type="number">
/// <item>an initial operator read. With no valid operator nothing is claimed, so a pending reminder waits for
/// the account to exist or be active again instead of being burned as stale;</item>
/// <item>the row's operator must be the current one — a row queued for another account is retired;</item>
/// <item>the <b>acquisition recheck</b>: a bundle the operator bought since the row was queued is discarded
/// (<see cref="BundleAlertStatuses.Suppressed"/>), not sent and not failed;</item>
/// <item>the <b>expiry revalidation</b>: the persisted expiry must still be exactly the snapshot the row was
/// queued for and still inside the window. A moved expiry, a vanished bundle, an expired one, or one whose date
/// disappeared all retire the row, because the next producer run owns the new cycle;</item>
/// <item>a second operator read immediately before the send, for the same reason the offer dispatcher does it:
/// the first read is a snapshot, not a cycle-long permission.</item>
/// </list>
///
/// <para>
/// <b>The unavoidable boundary.</b> A purchase can commit after gate 3 and before (or during) the HTTP call. No
/// lock is held across that call — the ntfy POST is external and unbounded, and holding a database transaction
/// or a global identity lock across it would turn a slow topic into a stalled pipeline. The window is therefore
/// real and accepted: the reminder is at-most one message on a bundle that had just been bought, and the row is
/// not retried because the sender already answered. Everything else (a purchase committed before the gate) is
/// suppressed.
/// </para>
///
/// <para>
/// Failures are recorded as an outcome name only. The sender never reads the response body, the URL and the
/// topic never reach the log or the row, and the retry backoff is the shared
/// <see cref="NtfyRetrySchedule"/> bounded by the same <see cref="NtfyOptions"/> prices as the offer alerts.
/// </para>
/// </summary>
public sealed class BundleAlertDispatcher(
    IBundleAlertOutbox outbox,
    IBundleAlertSource source,
    IBundlePurchaseGate purchases,
    INtfyAlertSender sender,
    IOptions<NtfyOptions> options,
    TimeProvider clock,
    ILogger<BundleAlertDispatcher> logger,
    IDealAlertRecipientScope recipientScope)
{
    public async Task<BundleAlertDispatchReport> DispatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            return new BundleAlertDispatchReport(0, 0, 0, 0, 0, 0);
        }

        var now = clock.GetUtcNow().UtcDateTime;

        // Initial gate: no valid operator means nothing is claimed, so the backlog is preserved.
        var recipient = await recipientScope.LoadAsync(cancellationToken);
        if (recipient is null)
        {
            logger.LogWarning("[ntfy] bundle dispatch skipped: no active notification operator configured");
            return new BundleAlertDispatchReport(0, 0, 0, 0, 0, 0);
        }

        var due = await outbox.ClaimDueAsync(settings.DispatchBatchSize, now, cancellationToken);

        var sent = 0;
        var retried = 0;
        var failed = 0;
        var stale = 0;
        var suppressed = 0;

        for (var index = 0; index < due.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alert = due[index];

            if (alert.UserId != recipient.UserId)
            {
                await outbox.MarkStaleAsync(alert, cancellationToken);
                stale++;
                continue;
            }

            // Acquisition recheck, before any expiry read and before the HTTP call.
            if (await purchases.IsAcquiredAsync(alert.UserId, alert.Source, alert.BundleKey, cancellationToken))
            {
                await outbox.MarkSuppressedAsync(alert, cancellationToken);
                suppressed++;
                continue;
            }

            if (!await StillCurrentAsync(alert, now, cancellationToken))
            {
                await outbox.MarkStaleAsync(alert, cancellationToken);
                stale++;
                continue;
            }

            // Last-moment operator recheck, immediately before the send.
            var current = await recipientScope.LoadAsync(cancellationToken);
            if (current is null || current.UserId != recipient.UserId)
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
                    // Retryable or Ambiguous. Ambiguous counts as retryable on purpose: the message may already
                    // be out, so the channel is at-least-once.
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
                "[ntfy] bundle dispatch claimed={Claimed} sent={Sent} retried={Retried} failed={Failed} stale={Stale} suppressed={Suppressed}",
                due.Count,
                sent,
                retried,
                failed,
                stale,
                suppressed);
        }

        return new BundleAlertDispatchReport(due.Count, sent, retried, failed, stale, suppressed);
    }

    /// <summary>
    /// The row is still worth sending: the bundle exists, its persisted expiry is <b>exactly</b> the snapshot the
    /// row was queued for, and that instant is still inside the red window at <paramref name="now"/>.
    ///
    /// The exact comparison is what protects a retry: a provider that moved the expiry made the stored text (and
    /// the day count in it) wrong, so the row is retired and the new cycle is queued by the next produce phase
    /// instead of pushing a stale date.
    /// </summary>
    private async Task<bool> StillCurrentAsync(BundleAlertEvent alert, DateTime now, CancellationToken cancellationToken)
    {
        var current = await source.FindCurrentAsync(alert.Source, alert.BundleKey, cancellationToken);
        if (current?.ExpiresAt is not { } expiresAt)
        {
            return false;
        }

        return ToUtc(expiresAt) == ToUtc(alert.ExpiresAt) &&
            BundleAlertPolicy.IsWithinAlertWindow(expiresAt, now);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
