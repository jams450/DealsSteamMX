#nullable enable
using Deals.API;
using Deals.API.Extensions;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Personal-operator filtering and dispatch safety (spec S4, S6). Everything runs through the public pipeline
/// with fakes: one operator's favorites + wishlist interest set, no claim without a valid operator, and a
/// last-moment scope recheck before every send. No DB, no HTTP, no real ntfy message.
/// </summary>
internal static class NtfyPipelineChecks
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    public static async Task RunAsync()
    {
        await ProducerStopsWithoutOperatorAsync();
        await DispatcherDoesNotClaimWithoutOperatorAsync();
        await ProducerOnlyEnqueuesOperatorInterestsAsync();
        await DispatcherRetiresRemovedInterestAsync();
        await DispatcherRetiresUnknownIdentityAsync();
        await DispatcherDeliversCurrentInterestAsync();
        await OperatorSwitchNeverReplaysSentRowsAsync();
        await DispatcherRechecksRemovedInterestBeforeSendAsync();
        await DispatcherRechecksDeactivatedAccountBeforeSendAsync();
        await DispatcherRejectsChangedOperatorBeforeSendAsync();
        await ContainerBuiltPipelineFailsClosedAsync();
        await ContainerBuiltPipelineUsesDefaultOperatorAsync();
        Console.WriteLine("PASS: ntfy pipeline checks");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    private static IOptions<NtfyOptions> Settings(bool enabled, int userId, int maxAttempts = 5) =>
        Options.Create(new NtfyOptions
        {
            Enabled = enabled,
            UserId = userId,
            MinSecondsBetweenSends = 0,
            MaxAttempts = maxAttempts,
            MaxSnapshotAgeHours = 72,
            MinViableDiscountPercent = 50,
            RetryBaseSeconds = 60,
            RetryMaxSeconds = 3_600,
            DispatchBatchSize = 20,
        });

    private static DealAlertProducer Producer(
        FakeCandidateReader reader,
        FakeOutbox outbox,
        FakeRecipientScope scope,
        bool enabled = true,
        int userId = 7) =>
        new(reader, outbox, Settings(enabled, userId), new FixedTimeProvider(Now), scope);

    private static DealAlertDispatcher Dispatcher(
        FakeOutbox outbox,
        IDealAlertCandidateSource source,
        FakeSender sender,
        FakeRecipientScope scope,
        bool enabled = true,
        int userId = 7) =>
        new(
            outbox,
            source,
            sender,
            Settings(enabled, userId),
            new FixedTimeProvider(Now),
            NullLogger<DealAlertDispatcher>.Instance,
            scope);

    private static DealAlertRecipient Recipient(long[] gameIds, int[] steamGameIds, int userId = 7) =>
        new(userId, gameIds.ToHashSet(), steamGameIds.ToHashSet());

    private static DealAlertCandidate Candidate(
        long? gameId = 100,
        int? steamGameId = 42,
        int price = 1_500,
        string source = "itad",
        string offerKey = "a") =>
        new(
            gameId,
            steamGameId,
            "Floppy Knights",
            source,
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

    /// <summary>Missing operator id: the producer must not even load the fresh snapshot set.</summary>
    private static async Task ProducerStopsWithoutOperatorAsync()
    {
        var reader = new FakeCandidateReader { Candidates = { Candidate() } };
        var outbox = new FakeOutbox();
        var scope = new FakeRecipientScope { Recipient = null };

        var report = await Producer(reader, outbox, scope, userId: 0).ProduceAsync(CancellationToken.None);

        Check(report.Candidates == 0 && report.Triggered == 0 && report.Enqueued == 0,
            "a missing operator produces nothing");
        Check(outbox.EnqueueCalls == 0 && outbox.Rows.Count == 0,
            "a missing operator never touches the outbox");
        Check(reader.LastRecipient is null, "no payload is loaded before the operator is validated");
    }

    /// <summary>Missing operator id: the dispatcher must not claim, so a pending row stays pending forever.</summary>
    private static async Task DispatcherDoesNotClaimWithoutOperatorAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = null };

        var report = await Dispatcher(outbox, source, sender, scope, userId: 0).DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 0 && report.Sent == 0 && report.Stale == 0,
            "a missing operator claims and retires nothing");
        Check(outbox.ClaimCalls == 0, "a missing operator never claims the outbox");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Pending, "the pending row is left untouched");
        Check(outbox.Rows[0].Attempts == 0, "a missing operator never burns an attempt");
        Check(sender.Sent.Count == 0, "a missing operator never sends");
    }

    /// <summary>
    /// The operator's private scope is the union of favorites (canonical game id) and wishlist rows
    /// (canonical game id or the exact Steam appid mapping). Anything else never reaches the outbox.
    /// </summary>
    private static async Task ProducerOnlyEnqueuesOperatorInterestsAsync()
    {
        var reader = new FakeCandidateReader
        {
            Candidates =
            {
                Candidate(gameId: 100, steamGameId: null, offerKey: "favorite"),
                Candidate(gameId: null, steamGameId: 42, offerKey: "wishlist"),
                Candidate(gameId: 200, steamGameId: 43, offerKey: "other"),
            },
        };
        var outbox = new FakeOutbox();
        var scope = new FakeRecipientScope { Recipient = Recipient([100], [42]) };

        var report = await Producer(reader, outbox, scope).ProduceAsync(CancellationToken.None);

        Check(report.Candidates == 3, "the reader still saw every fresh candidate");
        Check(report.Triggered == 2, "only the two operator interests fire");
        Check(report.Enqueued == 2, "only the two operator interests reach the outbox");
        Check(outbox.Rows.All(row => row.OfferKey != "other"),
            "an offer outside the operator scope never reaches the outbox");
        Check(reader.LastRecipient is not null,
            "the operator scope is handed to the reader so the payload query is filtered, not loaded whole");
    }

    /// <summary>An interest removed after enqueue (unfavorited / wishlist row gone) never sends.</summary>
    private static async Task DispatcherRetiresRemovedInterestAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = Recipient([], []) };

        var report = await Dispatcher(outbox, source, sender, scope).DispatchAsync(CancellationToken.None);
        var row = outbox.Rows[0];

        Check(report.Claimed == 1 && report.Stale == 1 && report.Sent == 0,
            "an out-of-scope pending row is retired, not sent");
        Check(row.Status == DealAlertStatuses.Stale, "the row is marked stale");
        Check(row.Attempts == 0, "retiring an out-of-scope row burns no attempt");
        Check(row.SentAt is null, "the retired row is not marked as sent");
        Check(sender.Sent.Count == 0, "an out-of-scope row is never sent");
    }

    /// <summary>Old backlog rows with no identity at all (both ids null) must never send.</summary>
    private static async Task DispatcherRetiresUnknownIdentityAsync()
    {
        var candidate = Candidate(gameId: null, steamGameId: null);
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = Recipient([100], [42]) };

        var report = await Dispatcher(outbox, source, sender, scope).DispatchAsync(CancellationToken.None);

        Check(report.Stale == 1 && report.Sent == 0, "an unknown-identity backlog row is retired");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Stale, "the unknown-identity row is stale");
        Check(sender.Sent.Count == 0, "an unknown-identity row is never sent");
    }

    /// <summary>A currently interesting, still-live deal is delivered.</summary>
    private static async Task DispatcherDeliversCurrentInterestAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = Recipient([100], []) };

        var report = await Dispatcher(outbox, source, sender, scope).DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 1 && report.Sent == 1, "the current interest is delivered");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Sent, "the delivered row is marked sent");
        Check(sender.Sent.Count == 1, "the sender is called once");
    }

    /// <summary>
    /// Switching the configured operator keeps the dedup state: a previously sent row for the same deal is
    /// never replayed (the documented limitation), and only pending in-scope rows are claimed.
    /// </summary>
    private static async Task OperatorSwitchNeverReplaysSentRowsAsync()
    {
        var sentCandidate = Candidate(price: 900, offerKey: "already-sent");
        var pendingCandidate = Candidate(price: 1_500, offerKey: "pending");
        var outbox = new FakeOutbox();
        var sentRow = Event(sentCandidate);
        sentRow.DealAlertEventId = 1;
        sentRow.Status = DealAlertStatuses.Sent;
        sentRow.SentAt = Now - TimeSpan.FromHours(2);
        outbox.Rows.Add(sentRow);
        var pendingRow = Event(pendingCandidate);
        pendingRow.DealAlertEventId = 2;
        outbox.Rows.Add(pendingRow);
        var source = new FakeCandidateReader { Current = pendingCandidate };
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = Recipient([100], [], userId: 8) };

        var report = await Dispatcher(outbox, source, sender, scope, userId: 8).DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 1 && report.Sent == 1, "only the pending row is claimed and sent");
        Check(sentRow.Status == DealAlertStatuses.Sent, "the already sent row keeps its status");
        Check(sentRow.SentAt == Now - TimeSpan.FromHours(2), "the already sent row keeps its sent_at");
        Check(sentRow.Attempts == 0, "the already sent row is never retried");
        Check(outbox.Rows.Count == 2, "no row is inserted or removed by a dispatch cycle");
    }

    /// <summary>
    /// The scope is a snapshot of one read, not a cycle-long permission. Interest removed while the cycle is
    /// running (unfavorite, wishlist row deleted) must be seen by the re-read that happens immediately before
    /// the send.
    /// </summary>
    private static async Task DispatcherRechecksRemovedInterestBeforeSendAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = Recipient([100], [42]) };
        // First read still sees the interest; the second read (just before the send) does not.
        scope.Script.Enqueue(Recipient([100], [42]));
        scope.Script.Enqueue(Recipient([], []));

        var report = await Dispatcher(outbox, source, sender, scope).DispatchAsync(CancellationToken.None);

        Check(report.Claimed == 1 && report.Stale == 1 && report.Sent == 0,
            "a row whose interest vanished mid-cycle is retired, not sent");
        Check(sender.Sent.Count == 0, "an interest removed before the send is never delivered");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Stale, "the row is stale");
        Check(outbox.Rows[0].Attempts == 0, "retiring it burns no attempt");
        Check(scope.LoadCalls == 2, "the scope is re-read immediately before the send");
    }

    /// <summary>The operator account is deactivated between the cycle start and the send.</summary>
    private static async Task DispatcherRechecksDeactivatedAccountBeforeSendAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = Recipient([100], [42]) };
        scope.Script.Enqueue(Recipient([100], [42]));
        scope.Script.Enqueue(null);

        var report = await Dispatcher(outbox, source, sender, scope).DispatchAsync(CancellationToken.None);

        Check(report.Sent == 0 && sender.Sent.Count == 0, "a deactivated account receives nothing");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Stale,
            "the claimed row is retired instead of sent when the account is gone");
        Check(outbox.Rows[0].Attempts == 0, "a deactivated account does not burn an attempt");
    }

    /// <summary>
    /// The configured operator changes mid-cycle: the row was claimed for the account read at cycle start, so a
    /// different account id is not allowed to receive it even when it shares the same interest set.
    /// </summary>
    private static async Task DispatcherRejectsChangedOperatorBeforeSendAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var source = new FakeCandidateReader { Current = candidate };
        var sender = new FakeSender();
        var scope = new FakeRecipientScope { Recipient = Recipient([100], [42], userId: 7) };
        scope.Script.Enqueue(Recipient([100], [42], userId: 7));
        scope.Script.Enqueue(Recipient([100], [42], userId: 8));

        var report = await Dispatcher(outbox, source, sender, scope).DispatchAsync(CancellationToken.None);

        Check(report.Sent == 0 && sender.Sent.Count == 0, "a changed operator never receives the claimed row");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Stale, "the row is retired when the operator changed");
    }

    /// <summary>
    /// The host wires the scope through DI; the constructor has no optional bypass. This resolves the real graph
    /// with the operator explicitly set to 0 (unset) and proves it still fails closed, without startup validation
    /// and without touching the database.
    /// </summary>
    private static async Task ContainerBuiltPipelineFailsClosedAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var reader = new FakeCandidateReader { Candidates = { candidate }, Current = candidate };
        var sender = new FakeSender();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiNtfyNotifications();
        // Replace the configuration-bound options so the gate is proven without startup validation.
        services.AddScoped<IOptions<NtfyOptions>>(_ => Settings(enabled: true, userId: 0));
        // The operator gate must answer before any query: a null repository proves nothing was read.
        services.AddScoped<IRepository>(_ => null!);
        services.AddScoped<IDealAlertOutbox>(_ => outbox);
        services.AddScoped<IDealAlertCandidateReader>(_ => reader);
        services.AddScoped<IDealAlertCandidateSource>(_ => reader);
        services.AddScoped<INtfyAlertSender>(_ => sender);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<DealAlertDispatcher>();
        var dispatch = await dispatcher.DispatchAsync(CancellationToken.None);
        Check(dispatch.Claimed == 0 && dispatch.Sent == 0, "the container-built dispatcher fails closed");
        Check(outbox.ClaimCalls == 0 && sender.Sent.Count == 0,
            "the container-built dispatcher never claims or sends without an operator");

        var producer = scope.ServiceProvider.GetRequiredService<DealAlertProducer>();
        var produce = await producer.ProduceAsync(CancellationToken.None);
        Check(produce.Candidates == 0 && produce.Enqueued == 0, "the container-built producer fails closed");
        Check(outbox.EnqueueCalls == 0, "the container-built producer never enqueues without an operator");
    }

    /// <summary>
    /// The counterpart control (spec S4/L15): with <b>no operator configuration at all</b> the real scope reads
    /// the single-user default account 1 and the cycle runs, so the default is a real operator instead of a
    /// silent "unset". Account 1 has no favorites/wishlist in this fake, so the claimed row is retired as stale
    /// and nothing is sent.
    /// </summary>
    private static async Task ContainerBuiltPipelineUsesDefaultOperatorAsync()
    {
        var candidate = Candidate();
        var outbox = new FakeOutbox();
        outbox.Rows.Add(Event(candidate));
        var reader = new FakeCandidateReader { Candidates = { candidate }, Current = candidate };
        var sender = new FakeSender();

        var repository = new FakeRepository();
        repository.Users.Add(new Deals.Models.Entities.User { UserId = 1, Active = true });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiNtfyNotifications();
        // Only the id is left unconfigured: everything else is the code default.
        services.AddScoped<IOptions<NtfyOptions>>(_ => Options.Create(new NtfyOptions
        {
            Enabled = true,
            MinSecondsBetweenSends = 0,
        }));
        services.AddScoped<IRepository>(_ => repository);
        services.AddScoped<IDealAlertOutbox>(_ => outbox);
        services.AddScoped<IDealAlertCandidateReader>(_ => reader);
        services.AddScoped<IDealAlertCandidateSource>(_ => reader);
        services.AddScoped<INtfyAlertSender>(_ => sender);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatch = await scope.ServiceProvider.GetRequiredService<DealAlertDispatcher>()
            .DispatchAsync(CancellationToken.None);

        Check(repository.GetCalls > 0, "the unconfigured pipeline resolves the default account 1 instead of stopping at an unset gate");
        Check(outbox.ClaimCalls == 1, "the unconfigured pipeline claims, because the default operator is a valid operator");
        Check(dispatch.Sent == 0 && sender.Sent.Count == 0,
            "account 1 has no interest in this fake, so the claimed row is not delivered");
        Check(outbox.Rows[0].Status == DealAlertStatuses.Stale, "the out-of-interest row is retired as stale");
    }
}
