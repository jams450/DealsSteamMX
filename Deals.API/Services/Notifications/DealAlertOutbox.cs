using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Services.Notifications;

/// <summary>
/// Durable outbox for the alert rows. The interface exists so the dispatcher can be checked with an
/// in-memory fake: the real implementation is a thin, tracked EF query set, and the dedup guarantee
/// lives in the database unique index, not in this class.
/// </summary>
public interface IDealAlertOutbox
{
    /// <summary>
    /// Inserts the firing decisions whose dedup key is not present yet. Rejected candidates never reach
    /// this method, so a rejection cannot mutate the outbox. Returns the number of rows actually added.
    /// </summary>
    Task<int> TryEnqueueAsync(
        IReadOnlyList<DealAlertDecision> decisions,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    /// <summary>Due pending rows, oldest first, bounded by <paramref name="batchSize"/>. Tracked for update.</summary>
    Task<IReadOnlyList<DealAlertEvent>> ClaimDueAsync(int batchSize, DateTime nowUtc, CancellationToken cancellationToken);

    Task MarkSentAsync(DealAlertEvent alert, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Records a retryable failure and bumps the attempt counter.</summary>
    Task MarkRetryAsync(DealAlertEvent alert, string error, DateTime nextAttemptUtc, CancellationToken cancellationToken);

    /// <summary>Retires a row after a permanent rejection or an exhausted attempt budget.</summary>
    Task MarkFailedAsync(DealAlertEvent alert, string error, CancellationToken cancellationToken);

    /// <summary>Retires a row whose live offer no longer matches. Not a failure and does not bump attempts.</summary>
    Task MarkStaleAsync(DealAlertEvent alert, CancellationToken cancellationToken);
}

/// <summary>EF implementation over <c>deal_alert_events</c>.</summary>
public sealed class EfDealAlertOutbox(IRepository repository) : IDealAlertOutbox
{
    public async Task<int> TryEnqueueAsync(
        IReadOnlyList<DealAlertDecision> decisions,
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

        var existing = await repository.Get<DealAlertEvent>()
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

            repository.GetTrack<DealAlertEvent>().Add(ToEvent(decision, nowUtc));
            added++;
        }

        if (added > 0)
        {
            await repository.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    public async Task<IReadOnlyList<DealAlertEvent>> ClaimDueAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await repository.GetTrack<DealAlertEvent>()
            .Where(alert => alert.Status == DealAlertStatuses.Pending && alert.NextAttemptAt <= nowUtc)
            .OrderBy(alert => alert.NextAttemptAt)
            .ThenBy(alert => alert.DealAlertEventId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    public async Task MarkSentAsync(DealAlertEvent alert, DateTime nowUtc, CancellationToken cancellationToken)
    {
        alert.Status = DealAlertStatuses.Sent;
        alert.SentAt = nowUtc;
        alert.LastError = null;
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkRetryAsync(
        DealAlertEvent alert,
        string error,
        DateTime nextAttemptUtc,
        CancellationToken cancellationToken)
    {
        alert.Attempts++;
        alert.LastError = Truncate(error);
        alert.NextAttemptAt = nextAttemptUtc;
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(DealAlertEvent alert, string error, CancellationToken cancellationToken)
    {
        alert.Attempts++;
        alert.Status = DealAlertStatuses.Failed;
        alert.LastError = Truncate(error);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkStaleAsync(DealAlertEvent alert, CancellationToken cancellationToken)
    {
        alert.Status = DealAlertStatuses.Stale;
        await repository.SaveChangesAsync(cancellationToken);
    }

    private static DealAlertEvent ToEvent(DealAlertDecision decision, DateTime nowUtc) => new()
    {
        DedupKey = decision.DedupKey,
        SteamGameId = decision.Candidate.SteamGameId,
        GameId = decision.Candidate.GameId,
        Source = decision.Candidate.Source,
        OfferKey = decision.Candidate.OfferKey,
        PriceMinor = decision.Candidate.MxnCurrentPriceMinor,
        TriggerVersion = DealAlertPolicy.TriggerVersion,
        Reasons = string.Join(',', decision.Reasons),
        Score = decision.Score is { } score ? (decimal?)Math.Round(score, 1, MidpointRounding.AwayFromZero) : null,
        Title = decision.Message?.Title ?? decision.Candidate.GameName,
        Message = decision.Message?.Body ?? string.Empty,
        ClickUrl = decision.Message?.ClickUrl,
        Status = DealAlertStatuses.Pending,
        Attempts = 0,
        NextAttemptAt = nowUtc,
    };

    private static string Truncate(string value) => value.Length <= 256 ? value : value[..256];
}
