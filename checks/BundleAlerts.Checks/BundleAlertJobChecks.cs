#nullable enable
using Deals.API;
using Deals.API.Services.Jobs;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// The reminder is a phase of the cycle that already exists, not a service nobody calls: the scheduled ntfy
/// run produces and dispatches both families under the same advisory lock and the same job options, and the
/// offer pipeline keeps its interest gate while bundle reminders go out without one.
/// </summary>
internal static class BundleAlertJobChecks
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    public static async Task RunAsync()
    {
        await ScheduledCycleRunsBothFamiliesAsync();
        await ProduceOnlyCycleSendsNothingAsync();
        await OfferInterestGateSurvivesBundlePhaseAsync();
        await BundleFailureDoesNotStopOfferPhaseAsync();
        Console.WriteLine("PASS: bundle alert job wiring checks");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    private static IOptions<NtfyOptions> Settings() =>
        Options.Create(new NtfyOptions
        {
            Enabled = true,
            UserId = 7,
            MinSecondsBetweenSends = 0,
            MaxAttempts = 3,
            RetryBaseSeconds = 60,
            RetryMaxSeconds = 3_600,
            DispatchBatchSize = 20,
            MaxSnapshotAgeHours = 72,
            MinViableDiscountPercent = 50,
        });

    private sealed record Harness(
        NtfyDealsJobExecutor Executor,
        FakeBundleAlertSource BundleSource,
        FakeBundleAlertOutbox BundleOutbox,
        FakeDealAlertReader OfferReader,
        FakeDealAlertOutbox OfferOutbox,
        FakeSender Sender);

    private static Harness Build(FakeBundleAlertSource? bundleSource = null, FakeRecipientScope? scope = null)
    {
        var settings = Settings();
        var clock = new FixedTimeProvider(Now);
        var recipient = scope ?? new FakeRecipientScope
        {
            Recipient = new DealAlertRecipient(7, new HashSet<long>(), new HashSet<int>())
        };

        var offerReader = new FakeDealAlertReader();
        var offerOutbox = new FakeDealAlertOutbox();
        var sender = new FakeSender();

        var producer = new DealAlertProducer(offerReader, offerOutbox, settings, clock, recipient);
        var dispatcher = new DealAlertDispatcher(
            offerOutbox,
            offerReader,
            sender,
            settings,
            clock,
            NullLogger<DealAlertDispatcher>.Instance,
            recipient);

        var source = bundleSource ?? new FakeBundleAlertSource();
        var bundleOutbox = new FakeBundleAlertOutbox();
        var purchases = new FakeBundlePurchaseGate();
        var bundleProducer = new BundleAlertProducer(source, bundleOutbox, purchases, settings, clock, recipient);
        var bundleDispatcher = new BundleAlertDispatcher(
            bundleOutbox,
            source,
            purchases,
            sender,
            settings,
            clock,
            NullLogger<BundleAlertDispatcher>.Instance,
            recipient);

        var executor = new NtfyDealsJobExecutor(producer, dispatcher, bundleProducer, bundleDispatcher,
            NullLogger<NtfyDealsJobExecutor>.Instance);

        return new Harness(executor, source, bundleOutbox, offerReader, offerOutbox, sender);
    }

    private static BundleAlertCandidate Bundle(string bundleKey, DateTime expiresAt) =>
        new(
            "itad",
            bundleKey,
            "Prestige Collection",
            "Fanatical",
            "https://isthereanydeal.com/bundle/1",
            "https://www.fanatical.com/es/bundle/1?ref=itad",
            expiresAt,
            Now);

    private static T Field<T>(Dictionary<string, object?> details, string key)
        where T : notnull
    {
        if (!details.TryGetValue(key, out var value) || value is not T typed)
        {
            throw new Exception($"the cycle details must carry '{key}'");
        }

        return typed;
    }

    /// <summary>A nullable string detail: the key must exist, the value may be null.</summary>
    private static string? Text(Dictionary<string, object?> details, string key)
    {
        if (!details.TryGetValue(key, out var value))
        {
            throw new Exception($"the cycle details must carry '{key}'");
        }

        return value as string;
    }

    /// <summary>The scheduled cycle produces and dispatches the bundle family in the same run as the offers.</summary>
    private static async Task ScheduledCycleRunsBothFamiliesAsync()
    {
        var bundle = Bundle("bundle-1", Now.AddHours(50));
        var harness = Build();
        harness.BundleSource.Pending.Add(bundle);
        harness.BundleSource.Current[(bundle.Source, bundle.BundleKey)] =
            new(bundle.Source, bundle.BundleKey, bundle.ExpiresAt);

        var result = await harness.Executor.ExecuteAsync(NtfyDealsJobExecutor.ScheduledOptions, CancellationToken.None);
        var details = (Dictionary<string, object?>)result.Details!;
        var bundles = Field<Dictionary<string, object?>>(details, "bundles");

        Check(result.Succeeded, "a cycle with a queueable reminder succeeds");
        Check(Field<bool>(bundles, "produced") && Field<bool>(bundles, "dispatched"),
            "the scheduled cycle drives both bundle phases");
        Check(Field<int>(bundles, "enqueued") == 1, "the red bundle is queued by the cycle");
        Check(Field<int>(bundles, "sent") == 1, "the queued bundle reminder is dispatched in the same cycle");
        Check(harness.BundleOutbox.Rows.Single().Status == "sent", "the row ends as sent");

        // The offer counters keep their legacy names and their meaning: the new phase is additive.
        foreach (var legacy in new[] { "produced", "dispatched", "candidates", "triggered", "enqueued", "claimed", "sent", "retried", "failed", "stale" })
        {
            Check(details.ContainsKey(legacy), $"the legacy offer counter '{legacy}' is preserved");
        }
    }

    /// <summary>A produce-only manual run queues but sends nothing, for both families.</summary>
    private static async Task ProduceOnlyCycleSendsNothingAsync()
    {
        var bundle = Bundle("bundle-1", Now.AddHours(50));
        var harness = Build();
        harness.BundleSource.Pending.Add(bundle);

        var produceOnly = new JobRunOptions(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JobOptionKeys.Produce] = true
        });
        var result = await harness.Executor.ExecuteAsync(produceOnly, CancellationToken.None);
        var details = (Dictionary<string, object?>)result.Details!;
        var bundles = Field<Dictionary<string, object?>>(details, "bundles");

        Check(Field<int>(bundles, "enqueued") == 1, "the produce phase queues the red bundle");
        Check(!Field<bool>(bundles, "dispatched"), "the produce-only run does not dispatch bundles");
        Check(harness.BundleOutbox.Rows.Single().Status == "pending", "the queued row stays pending");
        Check(harness.Sender.Sent.Count == 0, "a produce-only run sends nothing at all");
    }

    /// <summary>
    /// The offer pipeline keeps its interest gate (an unrelated offer is still dropped) while the bundle
    /// reminder goes out for the same empty-interest operator: the explicit "all pending bundles" request is
    /// an exception scoped to the new family, never a weakening of the offer gate.
    /// </summary>
    private static async Task OfferInterestGateSurvivesBundlePhaseAsync()
    {
        var unrelated = new DealAlertCandidate(
            900,
            900,
            "Unrelated Game",
            "itad",
            "offer-1",
            "Shop",
            "authorized",
            "regional",
            "mx",
            "MXN",
            1_000,
            1_000,
            null,
            null,
            Now,
            100_000,
            "MXN",
            null,
            null,
            "https://example.com/deal");
        var harness = Build();
        harness.OfferReader.Candidates.Add(unrelated);
        harness.BundleSource.Pending.Add(Bundle("bundle-1", Now.AddHours(50)));

        var result = await harness.Executor.ExecuteAsync(NtfyDealsJobExecutor.ScheduledOptions, CancellationToken.None);
        var details = (Dictionary<string, object?>)result.Details!;

        Check(Field<int>(details, "enqueued") == 0,
            "the offer outside the operator's interests is still not queued");
        Check(harness.OfferOutbox.EnqueueCalls == 1 && harness.OfferOutbox.Rows.Count == 0,
            "the offer phase ran and rejected the candidate");
        Check(Field<Dictionary<string, object?>>(details, "bundles").ContainsKey("enqueued"),
            "the bundle phase is reported separately from the offer phase");
        Check(harness.BundleOutbox.Rows.Count == 1,
            "the bundle reminder is queued without any wishlist or favorite");
    }

    /// <summary>
    /// The bundle phase is additive: a failing bundle read is recorded and does not stop the offer phases.
    /// </summary>
    private static async Task BundleFailureDoesNotStopOfferPhaseAsync()
    {
        var source = new FakeBundleAlertSource { LoadFailure = new InvalidOperationException("boom") };
        var harness = Build(source);

        var result = await harness.Executor.ExecuteAsync(NtfyDealsJobExecutor.ScheduledOptions, CancellationToken.None);
        var details = (Dictionary<string, object?>)result.Details!;
        var bundles = Field<Dictionary<string, object?>>(details, "bundles");

        Check(!result.Succeeded, "a failed bundle phase is reported as a failed cycle");
        Check(!bundles.ContainsKey("produceError") || Text(bundles, "produceError") == nameof(InvalidOperationException),
            "the failure is recorded as a type name only");
        Check(Text(bundles, "produceError") == nameof(InvalidOperationException),
            "the recorded failure is the exception type, never its message");
        Check(Field<bool>(details, "dispatched"), "the offer dispatch phase still ran");
        Check(harness.OfferOutbox.Rows.Count == 0 && harness.Sender.Sent.Count == 0,
            "no offer was invented by the failing phase");
    }
}
