#nullable enable
using System.Net;
using Deals.API;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Models.Notifications;
using Deals.Models.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// The two phases of the red bundle reminder (spec S2) through the public pipeline: what is queued, what is
/// discarded when the bundle was bought, and what is retired when the snapshot behind a row moved. Everything
/// runs on fakes; the only HTTP that exists is the real sender over a fake handler.
/// </summary>
internal static class BundleAlertPipelineChecks
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    public static async Task RunAsync()
    {
        await ProducerStopsWithoutOperatorAsync();
        await DispatcherClaimsNothingWithoutOperatorAsync();
        await ProducerIgnoresInterestSetsAsync();
        await ProducerFiltersWindowAsync();
        await ProducerRepeatsNothingAcrossCyclesAsync();
        await ProducerCollapsesSourceFamilyAsync();
        await ProducerKeepsGgDealsApartAsync();
        await ProducerNeverMergesByTitleAsync();
        await ProducerSuppressesAcquiredBundleAsync();
        await ProducerNeverSuppressesAnotherAccountAsync();
        await DispatcherSuppressesBundleBoughtBeforeDispatchAsync();
        await DispatcherRetiresChangedExpiryAsync();
        await DispatcherRetiresExpiredOrUndatedBundleAsync();
        await DispatcherRetiresVanishedBundleAsync();
        await DispatcherSendsCurrentBundleAsync();
        await DispatcherRetiresWhenOperatorChangedAsync();
        await DispatcherRetriesWithBackoffAsync();
        await DispatcherFailsAfterExhaustedBudgetAsync();
        await DispatcherFailsPermanentlyAsync();
        await SenderUsesOnlyHttpsClickAsync();
        await SenderReportsSanitizedOutcomeAsync();
        Console.WriteLine("PASS: bundle alert pipeline checks");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    private static IOptions<NtfyOptions> Settings(bool enabled = true, int userId = 7, int maxAttempts = 3) =>
        Options.Create(new NtfyOptions
        {
            Enabled = enabled,
            UserId = userId,
            MinSecondsBetweenSends = 0,
            MaxAttempts = maxAttempts,
            RetryBaseSeconds = 60,
            RetryMaxSeconds = 3_600,
            DispatchBatchSize = 20,
        });

    private static BundleAlertCandidate Bundle(
        string bundleKey = "bundle-1",
        string source = "itad",
        string title = "Prestige Collection",
        DateTime? expiresAt = null,
        string? dealUrl = "https://www.fanatical.com/es/bundle/1?ref=itad",
        string? pageUrl = "https://isthereanydeal.com/bundle/1") =>
        new(
            source,
            bundleKey,
            title,
            "Fanatical",
            pageUrl,
            dealUrl,
            expiresAt ?? Now.AddHours(50),
            Now);

    private static BundleAlertProducer Producer(
        IBundleAlertSource source,
        IBundleAlertOutbox outbox,
        IBundlePurchaseGate purchases,
        IDealAlertRecipientScope scope,
        bool enabled = true,
        int userId = 7,
        int maxAttempts = 3) =>
        new(source, outbox, purchases, Settings(enabled, userId, maxAttempts), new FixedTimeProvider(Now), scope);

    private static BundleAlertDispatcher Dispatcher(
        IBundleAlertOutbox outbox,
        IBundleAlertSource source,
        IBundlePurchaseGate purchases,
        INtfyAlertSender sender,
        IDealAlertRecipientScope scope,
        bool enabled = true,
        int userId = 7,
        int maxAttempts = 3) =>
        new(
            outbox,
            source,
            purchases,
            sender,
            Settings(enabled, userId, maxAttempts),
            new FixedTimeProvider(Now),
            NullLogger<BundleAlertDispatcher>.Instance,
            scope);

    /// <summary>An empty interest set: bundle reminders never depend on favorites or the wishlist.</summary>
    private static DealAlertRecipient Recipient(int userId = 7, long[]? gameIds = null, int[]? steamGameIds = null) =>
        new(userId, (gameIds ?? []).ToHashSet(), (steamGameIds ?? []).ToHashSet());

    private static BundleAlertEvent PendingRow(BundleAlertCandidate candidate, int userId = 7, int attempts = 0) => new()
    {
        BundleAlertEventId = 1,
        DedupKey = BundleAlertPolicy.DedupKey(userId, candidate.Source, candidate.BundleKey, candidate.ExpiresAt),
        UserId = userId,
        Source = candidate.Source,
        SourceFamily = BundleAlertPolicy.SourceFamily(candidate.Source),
        BundleKey = candidate.BundleKey,
        ExpiresAt = candidate.ExpiresAt,
        TriggerVersion = BundleAlertPolicy.TriggerVersion,
        Title = "title",
        Message = "message",
        ClickUrl = candidate.DealUrl,
        Status = BundleAlertStatuses.Pending,
        Attempts = attempts,
        NextAttemptAt = Now,
    };

    /// <summary>No configured operator: nothing is read, queued, claimed or sent.</summary>
    private static async Task ProducerStopsWithoutOperatorAsync()
    {
        var source = new FakeBundleAlertSource { Pending = { Bundle() } };
        var outbox = new FakeBundleAlertOutbox();
        var purchases = new FakeBundlePurchaseGate();
        var scope = new FakeRecipientScope { Recipient = null };

        var report = await Producer(source, outbox, purchases, scope).ProduceAsync(CancellationToken.None);

        Check(report.Candidates == 0 && report.Triggered == 0 && report.Enqueued == 0,
            "a missing operator produces nothing");
        Check(source.LoadCalls == 0, "a missing operator never reads the bundle catalog");
        Check(purchases.Calls.Count == 0, "a missing operator never reads the acquisition table");
        Check(outbox.EnqueueCalls == 0 && outbox.Rows.Count == 0, "a missing operator never touches the outbox");
    }

    /// <summary>No configured operator: a queued row stays pending for the moment the account exists again.</summary>
    private static async Task DispatcherClaimsNothingWithoutOperatorAsync()
    {
        var candidate = Bundle();
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] = new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt);
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = null };

        var report = await Dispatcher(outbox, source, purchases: new FakeBundlePurchaseGate(), sender, scope, userId: 0)
            .DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 0 && report.Sent == 0 && report.Stale == 0,
            "a missing operator claims and retires nothing");
        Check(outbox.ClaimCalls == 0 && sender.Sent.Count == 0, "a missing operator never claims nor sends");
        Check(outbox.Rows[0].Status == BundleAlertStatuses.Pending && outbox.Rows[0].Attempts == 0,
            "the queued row is preserved untouched");
    }

    /// <summary>
    /// The clarification says "todos los bundles pendientes": the operator's favorites and wishlist are not
    /// part of the gate, so an operator with an empty interest set still gets every red bundle.
    /// </summary>
    private static async Task ProducerIgnoresInterestSetsAsync()
    {
        var source = new FakeBundleAlertSource { Pending = { Bundle() } };
        var outbox = new FakeBundleAlertOutbox();
        var purchases = new FakeBundlePurchaseGate();
        var scope = new FakeRecipientScope { Recipient = Recipient(gameIds: [], steamGameIds: []) };

        var report = await Producer(source, outbox, purchases, scope).ProduceAsync(CancellationToken.None);

        Check(report.Enqueued == 1 && outbox.Rows.Count == 1,
            "an empty favorite/wishlist set still receives the red bundle alert");
        Check(outbox.Rows[0].UserId == 7, "the row belongs to the configured operator");
    }

    /// <summary>Only red bundles are queued, and the window is enforced by the pipeline, not only by SQL.</summary>
    private static async Task ProducerFiltersWindowAsync()
    {
        var source = new FakeBundleAlertSource();
        source.Pending.Add(Bundle(bundleKey: "exactly-72h", expiresAt: Now.AddHours(72)));
        source.Pending.Add(Bundle(bundleKey: "73h", expiresAt: Now.AddHours(73)));
        source.Pending.Add(Bundle(bundleKey: "expired", expiresAt: Now.AddSeconds(-1)));
        source.Pending.Add(Bundle(bundleKey: "soon", expiresAt: Now.AddHours(1)));
        var outbox = new FakeBundleAlertOutbox();
        var scope = new FakeRecipientScope { Recipient = Recipient() };

        var report = await Producer(source, outbox, new FakeBundlePurchaseGate(), scope)
            .ProduceAsync(CancellationToken.None);

        Check(report.Candidates == 4, "every candidate the reader returned is counted");
        Check(report.Triggered == 2 && report.Enqueued == 2, "only the two red bundles are queued");
        Check(outbox.Rows.All(row => row.BundleKey is "exactly-72h" or "soon"),
            "73h and expired bundles never reach the outbox");
    }

    /// <summary>
    /// An undated bundle has no expiry cycle to alert on, and an expired one is no longer active. Both are
    /// retired before the HTTP call, so a queued row cannot outlive its window.
    /// </summary>
    private static async Task DispatcherRetiresExpiredOrUndatedBundleAsync()
    {
        var undated = Bundle(bundleKey: "undated");
        var expired = Bundle(bundleKey: "expired");
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(undated));
        outbox.Rows.Add(PendingRow(expired));
        var source = new FakeBundleAlertSource();
        source.Current[(undated.Source, undated.BundleKey)] =
            new(undated.Source, undated.BundleKey, null);
        source.Current[(expired.Source, expired.BundleKey)] =
            new(expired.Source, expired.BundleKey, Now.AddSeconds(-1));
        var sender = new FakeSender();

        var report = await Dispatcher(outbox, source, new FakeBundlePurchaseGate(), sender,
                new FakeRecipientScope { Recipient = Recipient() })
            .DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 2 && report.Stale == 2 && report.Sent == 0,
            "an undated and an expired bundle are both retired");
        Check(outbox.Rows.All(row => row.Status == BundleAlertStatuses.Stale), "both rows are stale");
        Check(outbox.Rows.All(row => row.Attempts == 0), "retiring them burns no attempt");
        Check(sender.Sent.Count == 0, "neither an undated nor an expired bundle is sent");
    }

    /// <summary>Repeating the job on an unchanged bundle inserts nothing: one reminder per expiry cycle.</summary>
    private static async Task ProducerRepeatsNothingAcrossCyclesAsync()
    {
        var source = new FakeBundleAlertSource { Pending = { Bundle() } };
        var outbox = new FakeBundleAlertOutbox();
        var purchases = new FakeBundlePurchaseGate();
        var scope = new FakeRecipientScope { Recipient = Recipient() };
        var producer = Producer(source, outbox, purchases, scope);

        var first = await producer.ProduceAsync(CancellationToken.None);
        var second = await producer.ProduceAsync(CancellationToken.None);
        var third = await producer.ProduceAsync(CancellationToken.None);

        Check(first.Enqueued == 1, "the first cycle queues one reminder");
        Check(second.Enqueued == 0 && third.Enqueued == 0, "a repeated cycle queues nothing again");
        Check(outbox.Rows.Count == 1, "the outbox holds exactly one row per dedup identity");
        Check(outbox.Rows[0].Attempts == 0 && outbox.Rows[0].Status == BundleAlertStatuses.Pending,
            "the repeated cycle does not touch the queued row");
    }

    /// <summary>itad and itad_global are one family: the same bundle key is one reminder, not two.</summary>
    private static async Task ProducerCollapsesSourceFamilyAsync()
    {
        var source = new FakeBundleAlertSource();
        source.Pending.Add(Bundle(source: "itad"));
        source.Pending.Add(Bundle(source: "itad_global"));
        var outbox = new FakeBundleAlertOutbox();

        var report = await Producer(source, outbox, new FakeBundlePurchaseGate(),
                new FakeRecipientScope { Recipient = Recipient() })
            .ProduceAsync(CancellationToken.None);

        Check(report.Enqueued == 1, "the two ITAD sources collapse into one reminder");
        Check(outbox.Rows[0].SourceFamily == "itad", "the row records the family, not the raw source");
    }

    /// <summary>gg.deals uses its own key space: the same key there is a different bundle and a second alert.</summary>
    private static async Task ProducerKeepsGgDealsApartAsync()
    {
        var source = new FakeBundleAlertSource();
        source.Pending.Add(Bundle(source: "itad"));
        source.Pending.Add(Bundle(source: "ggdeals", title: "Prestige Collection (GG)"));
        var outbox = new FakeBundleAlertOutbox();

        var report = await Producer(source, outbox, new FakeBundlePurchaseGate(),
                new FakeRecipientScope { Recipient = Recipient() })
            .ProduceAsync(CancellationToken.None);

        Check(report.Enqueued == 2, "gg.deals never collapses with an ITAD bundle");
        Check(outbox.Rows.Select(row => row.SourceFamily).OrderBy(family => family).SequenceEqual(["ggdeals", "itad"]),
            "each row keeps its own family");
    }

    /// <summary>A title is not identity: two bundle keys with the same title are two reminders.</summary>
    private static async Task ProducerNeverMergesByTitleAsync()
    {
        var source = new FakeBundleAlertSource();
        source.Pending.Add(Bundle(bundleKey: "bundle-1", title: "Prestige Collection"));
        source.Pending.Add(Bundle(bundleKey: "bundle-2", title: "Prestige Collection"));
        var outbox = new FakeBundleAlertOutbox();

        var report = await Producer(source, outbox, new FakeBundlePurchaseGate(),
                new FakeRecipientScope { Recipient = Recipient() })
            .ProduceAsync(CancellationToken.None);

        Check(report.Enqueued == 2, "the same title under two keys is two bundles");
        Check(outbox.Rows.Select(row => row.BundleKey).Distinct(StringComparer.Ordinal).Count() == 2,
            "both keys reach the outbox");
    }

    /// <summary>A bundle bought before the producer runs is never queued.</summary>
    private static async Task ProducerSuppressesAcquiredBundleAsync()
    {
        var source = new FakeBundleAlertSource();
        source.Pending.Add(Bundle(bundleKey: "bought"));
        source.Pending.Add(Bundle(bundleKey: "pending"));
        var outbox = new FakeBundleAlertOutbox();
        var purchases = new FakeBundlePurchaseGate();
        purchases.Acquired.Add((7, "itad", "bought"));

        var report = await Producer(source, outbox, purchases, new FakeRecipientScope { Recipient = Recipient() })
            .ProduceAsync(CancellationToken.None);

        Check(report.Enqueued == 1 && report.Suppressed == 1, "the acquired bundle is suppressed, not queued");
        Check(outbox.Rows.Single().BundleKey == "pending", "only the pending bundle is queued");
        Check(purchases.Calls.All(call => call.UserId == 7), "the acquisition query is scoped to the operator");
    }

    /// <summary>Another account's purchase never silences this account's reminder.</summary>
    private static async Task ProducerNeverSuppressesAnotherAccountAsync()
    {
        var source = new FakeBundleAlertSource { Pending = { Bundle(bundleKey: "bought-elsewhere") } };
        var outbox = new FakeBundleAlertOutbox();
        var purchases = new FakeBundlePurchaseGate();
        purchases.Acquired.Add((8, "itad", "bought-elsewhere"));

        var report = await Producer(source, outbox, purchases, new FakeRecipientScope { Recipient = Recipient(userId: 7) })
            .ProduceAsync(CancellationToken.None);

        Check(report.Enqueued == 1 && report.Suppressed == 0,
            "a purchase of another account is not a suppression for this operator");
        Check(outbox.Rows.Single().UserId == 7, "the queued row belongs to the configured operator");
    }

    /// <summary>
    /// The queued row is rechecked before the HTTP call: a purchase committed after the producer ran discards
    /// the reminder instead of pushing it. The row is retired, not failed, and burns no attempt.
    /// </summary>
    private static async Task DispatcherSuppressesBundleBoughtBeforeDispatchAsync()
    {
        var candidate = Bundle(bundleKey: "bought-late");
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] = new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt);
        var purchases = new FakeBundlePurchaseGate();
        purchases.Acquired.Add((7, "itad_global", "bought-late"));
        var sender = new FakeSender();

        var report = await Dispatcher(outbox, source, purchases, sender,
                new FakeRecipientScope { Recipient = Recipient() })
            .DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 1 && report.Suppressed == 1 && report.Sent == 0,
            "the acquired bundle is discarded at dispatch");
        Check(outbox.Rows[0].Status == BundleAlertStatuses.Suppressed, "the row is recorded as suppressed");
        Check(outbox.Rows[0].Attempts == 0 && outbox.Rows[0].SentAt is null,
            "a suppressed row is neither a retry nor a delivery");
        Check(sender.Sent.Count == 0, "nothing is sent for an acquired bundle");
        Check(source.FindCalls == 0, "a suppressed row never spends an expiry revalidation");
    }

    /// <summary>The provider moved the expiry: the snapshot in the row is stale and a new cycle owns the alert.</summary>
    private static async Task DispatcherRetiresChangedExpiryAsync()
    {
        var candidate = Bundle();
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] =
            new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt.AddHours(5));
        var sender = new FakeSender();

        var report = await Dispatcher(outbox, source, new FakeBundlePurchaseGate(), sender,
                new FakeRecipientScope { Recipient = Recipient() })
            .DispatchAsync(CancellationToken.None);

        Check(report.Stale == 1 && report.Sent == 0, "a changed expiry retires the queued row");
        Check(outbox.Rows[0].Status == BundleAlertStatuses.Stale, "the row is stale");
        Check(outbox.Rows[0].Attempts == 0, "retiring a stale row burns no attempt");
        Check(sender.Sent.Count == 0, "a stale row is never sent");
    }

    /// <summary>A bundle that vanished from the catalog has no expiry to announce.</summary>
    private static async Task DispatcherRetiresVanishedBundleAsync()
    {
        var candidate = Bundle(bundleKey: "gone");
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var sender = new FakeSender();

        var report = await Dispatcher(outbox, new FakeBundleAlertSource(), new FakeBundlePurchaseGate(), sender,
                new FakeRecipientScope { Recipient = Recipient() })
            .DispatchAsync(CancellationToken.None);

        Check(report.Stale == 1 && sender.Sent.Count == 0, "a vanished bundle is retired, not sent");
        Check(outbox.Rows[0].Status == BundleAlertStatuses.Stale, "the row is stale");
    }

    /// <summary>The current, still-red, not-acquired bundle is delivered.</summary>
    private static async Task DispatcherSendsCurrentBundleAsync()
    {
        var candidate = Bundle();
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] =
            new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt);
        var sender = new FakeSender();

        var report = await Dispatcher(outbox, source, new FakeBundlePurchaseGate(), sender,
                new FakeRecipientScope { Recipient = Recipient() })
            .DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 1 && report.Sent == 1, "the current red bundle is delivered");
        Check(outbox.Rows[0].Status == BundleAlertStatuses.Sent && outbox.Rows[0].SentAt == Now,
            "the delivered row is marked sent with the cycle instant");
        Check(sender.Sent.Count == 1 && sender.Sent[0].ClickUrl == candidate.DealUrl,
            "the queued message and its click target are the ones sent");
    }

    /// <summary>The operator switched mid-cycle: the row was claimed for another account id.</summary>
    private static async Task DispatcherRetiresWhenOperatorChangedAsync()
    {
        var candidate = Bundle();
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] =
            new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt);
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = Recipient(userId: 7) };
        scope.Script.Enqueue(Recipient(userId: 7));
        scope.Script.Enqueue(null);

        var report = await Dispatcher(outbox, source, new FakeBundlePurchaseGate(), sender, scope)
            .DispatchAsync(CancellationToken.None);

        Check(report.Sent == 0 && sender.Sent.Count == 0, "a changed operator receives nothing");
        Check(outbox.Rows[0].Status == BundleAlertStatuses.Stale, "the row is retired when the operator is gone");
        Check(scope.LoadCalls == 2, "the operator is re-read immediately before the send");
    }

    /// <summary>A retryable rejection keeps the row pending with exponential backoff.</summary>
    private static async Task DispatcherRetriesWithBackoffAsync()
    {
        var candidate = Bundle();
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] =
            new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt);
        var sender = new FakeSender();
        sender.Outcomes.Enqueue(NtfySendOutcome.Retryable);

        var report = await Dispatcher(outbox, source, new FakeBundlePurchaseGate(), sender,
                new FakeRecipientScope { Recipient = Recipient() })
            .DispatchAsync(CancellationToken.None);

        Check(report.Retried == 1 && report.Sent == 0, "a retryable rejection is retried");
        Check(outbox.Rows[0].Status == BundleAlertStatuses.Pending && outbox.Rows[0].Attempts == 1,
            "the row stays pending with one attempt");
        Check(outbox.Rows[0].NextAttemptAt == Now.AddSeconds(60), "the first retry waits the base delay");
        Check(outbox.Rows[0].LastError == nameof(NtfySendOutcome.Retryable),
            "the recorded failure is the outcome name, never a URL or a token");
    }

    /// <summary>The attempt budget bounds the retries, so a dead topic cannot retry forever.</summary>
    private static async Task DispatcherFailsAfterExhaustedBudgetAsync()
    {
        var candidate = Bundle();
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate, attempts: 2));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] =
            new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt);
        var sender = new FakeSender();
        sender.Outcomes.Enqueue(NtfySendOutcome.Retryable);

        var report = await Dispatcher(outbox, source, new FakeBundlePurchaseGate(), sender,
                new FakeRecipientScope { Recipient = Recipient() }, maxAttempts: 3)
            .DispatchAsync(CancellationToken.None);

        Check(report.Failed == 1 && report.Retried == 0, "the last allowed attempt retires the row as failed");
        Check(outbox.Rows[0].Status == BundleAlertStatuses.Failed && outbox.Rows[0].Attempts == 3,
            "the exhausted row is failed with the full attempt count");
    }

    /// <summary>A permanent rejection is not retried.</summary>
    private static async Task DispatcherFailsPermanentlyAsync()
    {
        var candidate = Bundle();
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] =
            new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt);
        var sender = new FakeSender();
        sender.Outcomes.Enqueue(NtfySendOutcome.Permanent);

        var report = await Dispatcher(outbox, source, new FakeBundlePurchaseGate(), sender,
                new FakeRecipientScope { Recipient = Recipient() })
            .DispatchAsync(CancellationToken.None);

        Check(report.Failed == 1 && outbox.Rows[0].Status == BundleAlertStatuses.Failed,
            "a permanent rejection fails the row immediately");
        Check(outbox.Rows[0].LastError == nameof(NtfySendOutcome.Permanent), "the failure kind is recorded");
    }

    /// <summary>
    /// The dispatcher uses the real ntfy client, so its Click header is bound by the same HTTPS rule. A bundle
    /// whose provider URL is not HTTPS is pushed without a click target instead of with a broken one.
    /// </summary>
    private static async Task SenderUsesOnlyHttpsClickAsync()
    {
        var insecure = Bundle(
            dealUrl: "http://www.fanatical.com/es/bundle/1",
            pageUrl: "javascript:alert(1)");
        var handler = new RecordingHandler(_ => Responses.Status(HttpStatusCode.OK));
        var sender = new NtfyAlertSender(
            new HttpClient(handler) { BaseAddress = new Uri("https://ntfy.example/") },
            Options.Create(new NtfyOptions { BaseUrl = "https://ntfy.example/", Topic = "deals" }),
            NullLogger<NtfyAlertSender>.Instance);

        var outcome = await sender.SendAsync(
            new DealAlertMessage(
                BundleAlertPolicy.BuildMessage(insecure, Now).Title,
                BundleAlertPolicy.BuildMessage(insecure, Now).Body,
                BundleAlertPolicy.BuildMessage(insecure, Now).ClickUrl),
            CancellationToken.None);

        Check(outcome == NtfySendOutcome.Delivered, "the fake ntfy answered a success status");
        Check(handler.Method == HttpMethod.Post, "the reminder is one POST");
        Check(handler.ContentType == "text/plain", "the body is plain text");
        Check(handler.Title is not null && handler.Title.Contains("Prestige Collection", StringComparison.Ordinal),
            "the bundle name travels in the Title header");
        Check(handler.Click is null, "a non-HTTPS click target is not sent as a header");
        Check(handler.Body is not null && handler.Body.Contains("3 días", StringComparison.Ordinal),
            "the explicit day count travels in the body");
    }

    /// <summary>
    /// A rejected delivery is reported as a fixed vocabulary word. The status code and the URL never reach the
    /// outbox, which is what keeps the topic and the base URL out of the database and the logs.
    /// </summary>
    private static async Task SenderReportsSanitizedOutcomeAsync()
    {
        var candidate = Bundle();
        var handler = new RecordingHandler(_ => Responses.Status(HttpStatusCode.InternalServerError));
        var sender = new NtfyAlertSender(
            new HttpClient(handler) { BaseAddress = new Uri("https://ntfy.example/") },
            Options.Create(new NtfyOptions { BaseUrl = "https://ntfy.example/", Topic = "secret-topic" }),
            NullLogger<NtfyAlertSender>.Instance);
        var outbox = new FakeBundleAlertOutbox();
        outbox.Rows.Add(PendingRow(candidate));
        var source = new FakeBundleAlertSource();
        source.Current[(candidate.Source, candidate.BundleKey)] =
            new(candidate.Source, candidate.BundleKey, candidate.ExpiresAt);

        var report = await Dispatcher(outbox, source, new FakeBundlePurchaseGate(), sender,
                new FakeRecipientScope { Recipient = Recipient() })
            .DispatchAsync(CancellationToken.None);

        var error = outbox.Rows[0].LastError;
        Check(report.Retried == 1, "a 5xx over the real client is a retryable failure");
        Check(error == nameof(NtfySendOutcome.Retryable), "the failure kind is the only thing recorded");
        Check(error is not null && !error.Contains("http", StringComparison.OrdinalIgnoreCase),
            "no URL fragment reaches the outbox error");
        Check(error is not null && !error.Contains("secret-topic", StringComparison.Ordinal),
            "the topic never reaches the outbox error");
    }
}
