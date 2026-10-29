#nullable enable
using System.Net;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;

/// <summary>Deterministic clock: every rule is measured against a fixed instant.</summary>
internal sealed class FixedTimeProvider(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

/// <summary>
/// Scripted operator scope. A null recipient is the fail-closed answer (no configured user, unknown account or
/// an inactive account). <see cref="Script"/> answers one read at a time, so a check can prove the dispatcher
/// re-reads the operator immediately before a send instead of trusting the cycle's first read.
/// </summary>
internal sealed class FakeRecipientScope : IDealAlertRecipientScope
{
    public DealAlertRecipient? Recipient { get; set; }

    public Queue<DealAlertRecipient?> Script { get; } = new();

    public int LoadCalls { get; private set; }

    public Task<DealAlertRecipient?> LoadAsync(CancellationToken cancellationToken)
    {
        LoadCalls++;
        return Task.FromResult(Script.Count > 0 ? Script.Dequeue() : Recipient);
    }
}

/// <summary>
/// Scripted bundle catalog. The pending set is what the producer would read from <c>external_bundles</c>; the
/// current map is what the dispatcher re-reads per claimed row. It deliberately returns rows the SQL source
/// would have filtered out, because the pipeline must re-apply the window itself.
/// </summary>
internal sealed class FakeBundleAlertSource : IBundleAlertSource
{
    public List<BundleAlertCandidate> Pending { get; } = [];

    /// <summary>Keyed by (source, bundle_key). A missing key means the bundle is gone from the catalog.</summary>
    public Dictionary<(string Source, string BundleKey), BundleAlertCurrent> Current { get; } = new();

    public int LoadCalls { get; private set; }
    public int FindCalls { get; private set; }

    /// <summary>When set, the read throws it (a failing phase, never a DB).</summary>
    public Exception? LoadFailure { get; set; }

    public Task<IReadOnlyList<BundleAlertCandidate>> LoadPendingAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        LoadCalls++;
        if (LoadFailure is not null)
        {
            throw LoadFailure;
        }

        return Task.FromResult<IReadOnlyList<BundleAlertCandidate>>(Pending);
    }

    public Task<BundleAlertCurrent?> FindCurrentAsync(
        string source,
        string bundleKey,
        CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult(Current.TryGetValue((source, bundleKey), out var current) ? current : null);
    }
}

/// <summary>
/// Scripted acquisition table. <see cref="Acquired"/> is keyed by (user, source, bundle_key) exactly like
/// <c>bundle_purchases</c>, so a purchase of one account can never silence another account's alert.
/// </summary>
internal sealed class FakeBundlePurchaseGate : IBundlePurchaseGate
{
    public HashSet<(int UserId, string Source, string BundleKey)> Acquired { get; } = [];

    public List<(int UserId, string Source, string BundleKey)> Calls { get; } = [];

    /// <summary>When set, every read throws it: a failing suppression query.</summary>
    public Exception? Failure { get; set; }

    public Task<bool> IsAcquiredAsync(
        int userId,
        string source,
        string bundleKey,
        CancellationToken cancellationToken)
    {
        Calls.Add((userId, source, bundleKey));
        if (Failure is not null)
        {
            throw Failure;
        }

        var hit = Acquired.Any(row =>
            row.UserId == userId &&
            string.Equals(row.BundleKey, bundleKey, StringComparison.Ordinal) &&
            string.Equals(BundleAlertPolicy.SourceFamily(row.Source), BundleAlertPolicy.SourceFamily(source), StringComparison.Ordinal));
        return Task.FromResult(hit);
    }
}

/// <summary>In-memory durable outbox with the same dedup and transition semantics as the EF implementation.</summary>
internal sealed class FakeBundleAlertOutbox : IBundleAlertOutbox
{
    public List<BundleAlertEvent> Rows { get; } = [];
    public int EnqueueCalls { get; private set; }
    public int ClaimCalls { get; private set; }
    public IReadOnlyList<BundleAlertDecision> LastDecisions { get; private set; } = [];

    public Task<int> TryEnqueueAsync(
        IReadOnlyList<BundleAlertDecision> decisions,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        EnqueueCalls++;
        LastDecisions = decisions;
        var keys = Rows.Select(row => row.DedupKey).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var decision in decisions)
        {
            if (!keys.Add(decision.DedupKey))
            {
                continue;
            }

            Rows.Add(new BundleAlertEvent
            {
                BundleAlertEventId = Rows.Count + 1,
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
            });
            added++;
        }

        return Task.FromResult(added);
    }

    public Task<IReadOnlyList<BundleAlertEvent>> ClaimDueAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ClaimCalls++;
        return Task.FromResult<IReadOnlyList<BundleAlertEvent>>(
            Rows.Where(row => row.Status == BundleAlertStatuses.Pending && row.NextAttemptAt <= nowUtc)
                .OrderBy(row => row.NextAttemptAt)
                .ThenBy(row => row.BundleAlertEventId)
                .Take(batchSize)
                .ToList());
    }

    public Task MarkSentAsync(BundleAlertEvent alert, DateTime nowUtc, CancellationToken cancellationToken)
    {
        alert.Status = BundleAlertStatuses.Sent;
        alert.SentAt = nowUtc;
        alert.LastError = null;
        return Task.CompletedTask;
    }

    public Task MarkRetryAsync(BundleAlertEvent alert, string error, DateTime nextAttemptUtc, CancellationToken cancellationToken)
    {
        alert.Attempts++;
        alert.LastError = error;
        alert.NextAttemptAt = nextAttemptUtc;
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(BundleAlertEvent alert, string error, CancellationToken cancellationToken)
    {
        alert.Attempts++;
        alert.Status = BundleAlertStatuses.Failed;
        alert.LastError = error;
        return Task.CompletedTask;
    }

    public Task MarkStaleAsync(BundleAlertEvent alert, CancellationToken cancellationToken)
    {
        alert.Status = BundleAlertStatuses.Stale;
        return Task.CompletedTask;
    }

    public Task MarkSuppressedAsync(BundleAlertEvent alert, CancellationToken cancellationToken)
    {
        alert.Status = BundleAlertStatuses.Suppressed;
        return Task.CompletedTask;
    }
}

/// <summary>Scripted sender: records the messages and returns queued outcomes in order.</summary>
internal sealed class FakeSender : INtfyAlertSender
{
    public Queue<NtfySendOutcome> Outcomes { get; } = new();
    public List<DealAlertMessage> Sent { get; } = [];

    public Task<NtfySendOutcome> SendAsync(DealAlertMessage message, CancellationToken cancellationToken)
    {
        Sent.Add(message);
        return Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : NtfySendOutcome.Delivered);
    }
}

/// <summary>Captures the outgoing request. Never reaches the network.</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpMethod? Method { get; private set; }
    public Uri? RequestUri { get; private set; }
    public string? AuthorizationScheme { get; private set; }
    public string? Title { get; private set; }
    public string? Click { get; private set; }
    public string? Body { get; private set; }
    public string? ContentType { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Method = request.Method;
        RequestUri = request.RequestUri;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        Title = request.Headers.TryGetValues("Title", out var titles) ? titles.FirstOrDefault() : null;
        Click = request.Headers.TryGetValues("Click", out var clicks) ? clicks.FirstOrDefault() : null;
        ContentType = request.Content?.Headers.ContentType?.MediaType;
        if (request.Content is not null)
        {
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return responder(request);
    }
}

internal static class Responses
{
    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
}

// --- Offer-side doubles ---------------------------------------------------------------------------
// The ntfy job runs both families in one cycle, so the executor check needs the offer pipeline too. These
// doubles are inert: they prove the bundle phase did not change the offer phase, nothing else.

internal sealed class FakeDealAlertReader : IDealAlertCandidateReader
{
    public List<DealAlertCandidate> Candidates { get; } = [];
    public DealAlertCandidate? Current { get; set; }

    public Task<IReadOnlyList<DealAlertCandidate>> LoadFreshAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealAlertCandidate>>(Candidates);

    public Task<IReadOnlyList<DealAlertCandidate>> LoadFreshForRecipientAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        DealAlertRecipient recipient,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealAlertCandidate>>(Candidates);

    public Task<DealAlertCandidate?> FindCurrentAsync(DealAlertEvent alert, CancellationToken cancellationToken) =>
        Task.FromResult(Current);
}

internal sealed class FakeDealAlertOutbox : IDealAlertOutbox
{
    public List<DealAlertEvent> Rows { get; } = [];
    public int EnqueueCalls { get; private set; }

    public Task<int> TryEnqueueAsync(
        IReadOnlyList<DealAlertDecision> decisions,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        EnqueueCalls++;
        var keys = Rows.Select(row => row.DedupKey).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var decision in decisions)
        {
            if (!keys.Add(decision.DedupKey))
            {
                continue;
            }

            Rows.Add(new DealAlertEvent
            {
                DealAlertEventId = Rows.Count + 1,
                DedupKey = decision.DedupKey,
                Source = decision.Candidate.Source,
                OfferKey = decision.Candidate.OfferKey,
                PriceMinor = decision.Candidate.MxnCurrentPriceMinor,
                TriggerVersion = DealAlertPolicy.TriggerVersion,
                Reasons = string.Join(',', decision.Reasons),
                Title = decision.Message?.Title ?? decision.Candidate.GameName,
                Message = decision.Message?.Body ?? string.Empty,
                ClickUrl = decision.Message?.ClickUrl,
                Status = DealAlertStatuses.Pending,
                NextAttemptAt = nowUtc,
            });
            added++;
        }

        return Task.FromResult(added);
    }

    public Task<IReadOnlyList<DealAlertEvent>> ClaimDueAsync(int batchSize, DateTime nowUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealAlertEvent>>(
            Rows.Where(row => row.Status == DealAlertStatuses.Pending && row.NextAttemptAt <= nowUtc)
                .Take(batchSize)
                .ToList());

    public Task MarkSentAsync(DealAlertEvent alert, DateTime nowUtc, CancellationToken cancellationToken)
    {
        alert.Status = DealAlertStatuses.Sent;
        alert.SentAt = nowUtc;
        return Task.CompletedTask;
    }

    public Task MarkRetryAsync(DealAlertEvent alert, string error, DateTime nextAttemptUtc, CancellationToken cancellationToken)
    {
        alert.Attempts++;
        alert.LastError = error;
        alert.NextAttemptAt = nextAttemptUtc;
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(DealAlertEvent alert, string error, CancellationToken cancellationToken)
    {
        alert.Attempts++;
        alert.Status = DealAlertStatuses.Failed;
        alert.LastError = error;
        return Task.CompletedTask;
    }

    public Task MarkStaleAsync(DealAlertEvent alert, CancellationToken cancellationToken)
    {
        alert.Status = DealAlertStatuses.Stale;
        return Task.CompletedTask;
    }
}
