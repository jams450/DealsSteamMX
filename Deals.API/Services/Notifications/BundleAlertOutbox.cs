using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Services.Notifications;

/// <summary>
/// One reminder chosen by the producer: the operator it belongs to, the bundle snapshot, its dedup identity and
/// the text to send. Rejected candidates (outside the window, already acquired) never reach the outbox.
/// </summary>
public sealed record BundleAlertDecision(
    int UserId,
    BundleAlertCandidate Candidate,
    string DedupKey,
    BundleAlertMessage Message);

/// <summary>
/// Durable outbox of the bundle reminders, in its own table. The interface exists so both phases can be checked
/// with an in-memory fake; the real implementation is a thin tracked EF query set and the "once per expiry
/// cycle" guarantee lives in the database unique index on <c>dedup_key</c>, not in this class.
/// </summary>
public interface IBundleAlertOutbox
{
    /// <summary>
    /// Inserts the reminders whose dedup key is not present yet. Repeating the producer on an unchanged bundle
    /// inserts nothing, and a bundle key reported by both ITAD sources inserts once. Returns the rows added.
    /// </summary>
    Task<int> TryEnqueueAsync(
        IReadOnlyList<BundleAlertDecision> decisions,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>Due pending rows, oldest first, bounded by <paramref name="batchSize"/>. Tracked for update.</summary>
    Task<IReadOnlyList<BundleAlertEvent>> ClaimDueAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    Task MarkSentAsync(BundleAlertEvent alert, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Records a retryable failure and bumps the attempt counter.</summary>
    Task MarkRetryAsync(BundleAlertEvent alert, string error, DateTime nextAttemptUtc, CancellationToken cancellationToken);

    /// <summary>Retires a row after a permanent rejection or an exhausted attempt budget.</summary>
    Task MarkFailedAsync(BundleAlertEvent alert, string error, CancellationToken cancellationToken);

    /// <summary>Retires a row whose bundle no longer matches the snapshot. Not a failure, does not bump attempts.</summary>
    Task MarkStaleAsync(BundleAlertEvent alert, CancellationToken cancellationToken);

    /// <summary>
    /// Discards a row because the operator acquired the bundle. Not a failure and not a stale snapshot: the
    /// reason it must not be sent is the purchase, so it gets its own status.
    /// </summary>
    Task MarkSuppressedAsync(BundleAlertEvent alert, CancellationToken cancellationToken);
}

/// <summary>
/// EF implementation over <c>bundle_alert_events</c>, the same shape as <see cref="EfDealAlertOutbox"/>: the
/// producer inserts, the dispatcher transitions one claimed row at a time.
///
/// <para>
/// The insert is a key pre-check followed by an Add. The real guarantee is the unique index on <c>dedup_key</c>;
/// the pre-check only avoids a pointless insert. Two concurrent producers cannot both win because the ntfy cycle
/// runs under one PostgreSQL advisory lock (<c>NtfyDealsJobExecutor.AdvisoryLockKey</c>), which the scheduled
/// hosted service and the admin dispatcher share.
/// </para>
/// </summary>
public sealed class EfBundleAlertOutbox(IRepository repository) : IBundleAlertOutbox
{
    public async Task<int> TryEnqueueAsync(
        IReadOnlyList<BundleAlertDecision> decisions,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (decisions.Count == 0)
        {
            return 0;
        }

        var keys = decisions
            .Select(decision => decision.DedupKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existing = await repository.Get<BundleAlertEvent>()
            .Where(alert => keys.Contains(alert.DedupKey))
            .Select(alert => alert.DedupKey)
            .ToListAsync(cancellationToken);
        var seen = existing.ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var decision in decisions)
        {
            if (!seen.Add(decision.DedupKey))
            {
                continue;
            }

            repository.GetTrack<BundleAlertEvent>().Add(ToEvent(decision, nowUtc));
            added++;
        }

        if (added > 0)
        {
            await repository.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    public async Task<IReadOnlyList<BundleAlertEvent>> ClaimDueAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await repository.GetTrack<BundleAlertEvent>()
            .Where(alert => alert.Status == BundleAlertStatuses.Pending && alert.NextAttemptAt <= nowUtc)
            .OrderBy(alert => alert.NextAttemptAt)
            .ThenBy(alert => alert.BundleAlertEventId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    public async Task MarkSentAsync(BundleAlertEvent alert, DateTime nowUtc, CancellationToken cancellationToken)
    {
        alert.Status = BundleAlertStatuses.Sent;
        alert.SentAt = nowUtc;
        alert.LastError = null;
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkRetryAsync(
        BundleAlertEvent alert,
        string error,
        DateTime nextAttemptUtc,
        CancellationToken cancellationToken)
    {
        alert.Attempts++;
        alert.LastError = Truncate(error);
        alert.NextAttemptAt = nextAttemptUtc;
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(BundleAlertEvent alert, string error, CancellationToken cancellationToken)
    {
        alert.Attempts++;
        alert.Status = BundleAlertStatuses.Failed;
        alert.LastError = Truncate(error);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkStaleAsync(BundleAlertEvent alert, CancellationToken cancellationToken)
    {
        alert.Status = BundleAlertStatuses.Stale;
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSuppressedAsync(BundleAlertEvent alert, CancellationToken cancellationToken)
    {
        alert.Status = BundleAlertStatuses.Suppressed;
        await repository.SaveChangesAsync(cancellationToken);
    }

    private static BundleAlertEvent ToEvent(BundleAlertDecision decision, DateTime nowUtc) => new()
    {
        DedupKey = decision.DedupKey,
        UserId = decision.UserId,
        Source = decision.Candidate.Source,
        SourceFamily = BundleAlertPolicy.SourceFamily(decision.Candidate.Source),
        BundleKey = decision.Candidate.BundleKey,
        ExpiresAt = decision.Candidate.ExpiresAt,
        TriggerVersion = BundleAlertPolicy.TriggerVersion,
        Title = decision.Message.Title,
        Message = decision.Message.Body,
        ClickUrl = decision.Message.ClickUrl,
        Status = BundleAlertStatuses.Pending,
        Attempts = 0,
        NextAttemptAt = nowUtc,
    };

    private static string Truncate(string value) => value.Length <= 256 ? value : value[..256];
}
