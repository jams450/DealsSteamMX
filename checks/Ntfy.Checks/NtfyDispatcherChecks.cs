#nullable enable
using Deals.API;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Checks the dispatcher through fakes: stale revalidation, bounded retries, the ambiguous timeout path
/// and the guarantee that a rejected row keeps its data.
/// </summary>
internal static class NtfyDispatcherChecks
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    public static async Task RunAsync()
    {
        await DeliveredAsync();
        await StaleWhenOfferVanishedAsync();
        await StaleWhenPriceMovedAsync();
        await StaleWhenSnapshotExpiredAsync();
        await PermanentKeepsDataAsync();
        await RetryableSchedulesBackoffAsync();
        await AmbiguousIsRetriedAsync();
        await BoundedAttemptsFailAsync();
        RetrySchedule();
        Console.WriteLine("PASS: ntfy dispatcher checks");
    }

    private static void RetrySchedule()
    {
        var baseDelay = TimeSpan.FromSeconds(60);
        var maxDelay = TimeSpan.FromSeconds(3_600);
        Check(NtfyRetrySchedule.NextDelay(0, baseDelay, maxDelay) == TimeSpan.FromSeconds(60), "schedule floors at base");
        Check(NtfyRetrySchedule.NextDelay(1, baseDelay, maxDelay) == TimeSpan.FromSeconds(60), "first retry is base");
        Check(NtfyRetrySchedule.NextDelay(2, baseDelay, maxDelay) == TimeSpan.FromSeconds(120), "second retry doubles");
        Check(NtfyRetrySchedule.NextDelay(3, baseDelay, maxDelay) == TimeSpan.FromSeconds(240), "third retry doubles again");
        Check(NtfyRetrySchedule.NextDelay(20, baseDelay, maxDelay) == maxDelay, "schedule is capped");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    private static DealAlertCandidate Candidate(int price = 1_500, string offerKey = "a") =>
        new(
            100,
            42,
            "Floppy Knights",
            "itad",
            offerKey,
            "Shop",
            "authorized",
            "regional",
            "mx",
            "MXN",
            price,
            price,
            null,
            null,
            Now,
            100_000,
            "MXN",
            null,
            null,
            "https://example.com/deal");

    private static DealAlertEvent Event(DealAlertCandidate candidate, int attempts = 0) => new()
    {
        DealAlertEventId = 1,
        DedupKey = DealAlertPolicy.DedupKey(candidate),
        GameId = candidate.GameId,
        SteamGameId = candidate.SteamGameId,
        Source = candidate.Source,
        OfferKey = candidate.OfferKey,
        PriceMinor = candidate.MxnCurrentPriceMinor,
        TriggerVersion = DealAlertPolicy.TriggerVersion,
        Reasons = DealAlertReasons.Price,
        Title = "title",
        Message = "message",
        ClickUrl = candidate.DealUrl,
        Status = DealAlertStatuses.Pending,
        Attempts = attempts,
        NextAttemptAt = Now,
    };

    private static DealAlertDispatcher Dispatcher(
        FakeOutbox outbox,
        IDealAlertCandidateSource source,
        FakeSender sender,
        int maxAttempts = 5) =>
        new(
            outbox,
            source,
            sender,
            Options.Create(new NtfyOptions
            {
                MinSecondsBetweenSends = 0,
                MaxAttempts = maxAttempts,
                MaxSnapshotAgeHours = 72,
                MinViableDiscountPercent = 50,
                RetryBaseSeconds = 60,
                RetryMaxSeconds = 3_600,
                DispatchBatchSize = 20,
            }),
            new FixedTimeProvider(Now),
            NullLogger<DealAlertDispatcher>.Instance);

    private static async Task DeliveredAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        sender.Outcomes.Enqueue(NtfySendOutcome.Delivered);

        var report = await Dispatcher(outbox, source, sender).DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 1 && report.Sent == 1, "one claimed, one sent");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Sent, "row marked sent");
        Check(outbox.Rows[0].SentAt == Now, "sent_at set to cycle time");
        Check(sender.Sent.Count == 1, "sender called once");
    }

    private static async Task StaleWhenOfferVanishedAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = null };
        var sender = new FakeSender();

        var report = await Dispatcher(outbox, source, sender).DispatchAsync(CancellationToken.None);

        Check(report.Stale == 1 && report.Sent == 0, "vanished offer is stale");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Stale, "row retired as stale");
        Check(sender.Sent.Count == 0, "stale row is never sent");
    }

    private static async Task StaleWhenPriceMovedAsync()
    {
        var candidate = Candidate(price: 1_500);
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = Candidate(price: 1_400) };
        var sender = new FakeSender();

        var report = await Dispatcher(outbox, source, sender).DispatchAsync(CancellationToken.None);

        Check(report.Stale == 1, "changed price is stale");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Stale, "changed price retires the row");
        Check(sender.Sent.Count == 0, "changed price is never sent");
    }

    private static async Task StaleWhenSnapshotExpiredAsync()
    {
        var candidate = Candidate(price: 1_500);
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        // Same dedup identity, but the live snapshot is now stale: it no longer qualifies.
        var source = new FakeCandidateReader { Current = candidate with { ObservedAt = Now - TimeSpan.FromHours(73) } };
        var sender = new FakeSender();

        var report = await Dispatcher(outbox, source, sender).DispatchAsync(CancellationToken.None);

        Check(report.Stale == 1, "expired snapshot is stale");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Stale, "expired snapshot retires the row");
        Check(sender.Sent.Count == 0, "expired snapshot is never sent");
    }

    private static async Task PermanentKeepsDataAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        sender.Outcomes.Enqueue(NtfySendOutcome.Permanent);

        var report = await Dispatcher(outbox, source, sender).DispatchAsync(CancellationToken.None);
        var row = outbox.Rows[0];

        Check(report.Failed == 1, "permanent rejection counted as failed");
        Check(row.Status == DealAlertStatuses.Failed, "row marked failed");
        Check(row.Attempts == 1, "permanent failure recorded one attempt");
        Check(row.DedupKey == DealAlertPolicy.DedupKey(candidate), "dedup key unchanged");
        Check(row.PriceMinor == candidate.MxnCurrentPriceMinor, "price unchanged");
        Check(row.Message == "message", "message unchanged");
        Check(row.NextAttemptAt == Now, "failed row is not rescheduled");
    }

    private static async Task RetryableSchedulesBackoffAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        sender.Outcomes.Enqueue(NtfySendOutcome.Retryable);

        var report = await Dispatcher(outbox, source, sender).DispatchAsync(CancellationToken.None);
        var row = outbox.Rows[0];

        Check(report.Retried == 1, "retryable counted as retried");
        Check(row.Status == DealAlertStatuses.Pending, "retryable stays pending");
        Check(row.Attempts == 1, "attempt counter incremented");
        Check(row.NextAttemptAt == Now.AddSeconds(60), "first backoff is the base delay");
        Check(row.LastError == nameof(NtfySendOutcome.Retryable), "sanitized error kind stored");
    }

    private static async Task AmbiguousIsRetriedAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        sender.Outcomes.Enqueue(NtfySendOutcome.Ambiguous);

        var report = await Dispatcher(outbox, source, sender).DispatchAsync(CancellationToken.None);
        var row = outbox.Rows[0];

        Check(report.Sent == 0 && report.Retried == 1, "ambiguous timeout is retried, not marked sent");
        Check(row.Status == DealAlertStatuses.Pending, "ambiguous row stays pending");
        Check(row.Attempts == 1, "ambiguous attempt counted");
    }

    private static async Task BoundedAttemptsFailAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate, attempts: 1));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        sender.Outcomes.Enqueue(NtfySendOutcome.Retryable);

        var report = await Dispatcher(outbox, source, sender, maxAttempts: 2).DispatchAsync(CancellationToken.None);
        var row = outbox.Rows[0];

        Check(report.Failed == 1, "exhausted attempt budget fails");
        Check(row.Status == DealAlertStatuses.Failed, "row retired after exhausting retries");
        Check(row.Attempts == 2, "attempt counter reaches the budget");
    }
}
