#nullable enable
using System.Net;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;

/// <summary>Deterministic clock for the dispatcher and producer checks.</summary>
internal sealed class FixedTimeProvider(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

/// <summary>Candidate reader that returns a scripted snapshot and a scripted live offer.</summary>
internal sealed class FakeCandidateReader : IDealAlertCandidateReader
{
    public List<DealAlertCandidate> Candidates { get; } = [];
    public DealAlertCandidate? Current { get; set; }

    public Task<IReadOnlyList<DealAlertCandidate>> LoadFreshAsync(
        DateTime nowUtc,
        TimeSpan maxSnapshotAge,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealAlertCandidate>>(Candidates);

    public Task<DealAlertCandidate?> FindCurrentAsync(DealAlertEvent alert, CancellationToken cancellationToken) =>
        Task.FromResult(Current);
}

/// <summary>In-memory outbox with the same dedup/transition semantics as the EF implementation.</summary>
internal sealed class FakeOutbox : IDealAlertOutbox
{
    public List<DealAlertEvent> Rows { get; } = [];
    public int EnqueueCalls { get; private set; }
    public IReadOnlyList<DealAlertDecision> LastDecisions { get; private set; } = [];

    public Task<int> TryEnqueueAsync(
        IReadOnlyList<DealAlertDecision> decisions,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        EnqueueCalls++;
        LastDecisions = decisions;
        var keys = Rows.Select(row => row.DedupKey).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var decision in decisions)
        {
            if (!keys.Add(decision.DedupKey)) continue;
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
                Attempts = 0,
                NextAttemptAt = nowUtc,
            });
            added++;
        }

        return Task.FromResult(added);
    }

    public Task<IReadOnlyList<DealAlertEvent>> ClaimDueAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DealAlertEvent>>(
            Rows.Where(row => row.Status == DealAlertStatuses.Pending && row.NextAttemptAt <= nowUtc)
                .OrderBy(row => row.NextAttemptAt)
                .ThenBy(row => row.DealAlertEventId)
                .Take(batchSize)
                .ToList());

    public Task MarkSentAsync(DealAlertEvent alert, DateTime nowUtc, CancellationToken cancellationToken)
    {
        alert.Status = DealAlertStatuses.Sent;
        alert.SentAt = nowUtc;
        alert.LastError = null;
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

/// <summary>Scripted sender: records messages and returns queued outcomes.</summary>
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

/// <summary>Captures the outgoing request and returns a fresh response each call.</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpMethod? Method { get; private set; }
    public Uri? RequestUri { get; private set; }
    public string? AuthorizationScheme { get; private set; }
    public string? AuthorizationParameter { get; private set; }
    public string? Title { get; private set; }
    public string? Click { get; private set; }
    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Method = request.Method;
        RequestUri = request.RequestUri;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        Title = request.Headers.TryGetValues("Title", out var titles) ? titles.FirstOrDefault() : null;
        Click = request.Headers.TryGetValues("Click", out var clicks) ? clicks.FirstOrDefault() : null;
        if (request.Content is not null)
        {
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return responder(request);
    }
}

/// <summary>Handler that simulates a timeout or a connection failure.</summary>
internal sealed class ThrowingHandler(Func<HttpRequestMessage, Exception> exceptionFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(exceptionFactory(request));
}

internal static class Responses
{
    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
}
