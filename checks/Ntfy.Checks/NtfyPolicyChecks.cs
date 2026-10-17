#nullable enable
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;

/// <summary>
/// Behaviour checks for the pure alert policy and the producer's reject-before-enqueue contract.
/// Everything runs through the public surface; no DB, no HTTP, no real ntfy send.
/// </summary>
internal static class NtfyPolicyChecks
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DealAlertContext Context = new(Now, TimeSpan.FromHours(72), 50);

    public static void Run()
    {
        FormulaConsistency();
        ThresholdBoundaries();
        OrRules();
        HistoryLowEvidence();
        InvalidStaleAndCurrency();
        DedupIdentity();
        MessagesAndBatch();
        ProducerRejectsBeforeEnqueue();
        Console.WriteLine("PASS: ntfy policy checks");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    private static void CheckEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    private static DealAlertCandidate Candidate(
        int price = 5_000,
        string source = "itad",
        string offerKey = "offer",
        string shop = "Shop",
        string classification = "authorized",
        string pricing = "regional",
        string originalCurrency = "MXN",
        int? originalPrice = null,
        int? historyLow = null,
        string? historyCurrency = null,
        DateTime? observedAt = null,
        int? basePrice = 10_000,
        string? baseCurrency = "MXN",
        int? mxnLow = null,
        string? mxnLowCurrency = null,
        string? dealUrl = null,
        int? steamGameId = 42,
        long? gameId = 100) =>
        new(
            gameId,
            steamGameId,
            "Floppy Knights",
            source,
            offerKey,
            shop,
            classification,
            pricing,
            "mx",
            originalCurrency,
            originalPrice ?? price,
            price,
            historyLow,
            historyCurrency,
            observedAt ?? Now,
            basePrice,
            baseCurrency,
            mxnLow,
            mxnLowCurrency,
            dealUrl);

    /// <summary>Mirrors every assertion of Deals.Web/app/wishlist/_lib/wishlist-metrics.test.ts.</summary>
    private static void FormulaConsistency()
    {
        CheckEqual(75d, DealAlertPolicy.DiscountPercent(10_000, "MXN", 2_500), "discount 75");
        CheckEqual(88.9d, DealAlertPolicy.DiscountPercent(9_000, "MXN", 1_000), "discount round 88.9");
        CheckEqual(100d, DealAlertPolicy.DiscountPercent(10_000, "MXN", 0), "discount free 100");
        Check(DealAlertPolicy.DiscountPercent(10_000, "USD", 5_000) is null, "discount non-MXN null");
        Check(DealAlertPolicy.DiscountPercent(10_000, null, 5_000) is null, "discount null currency");
        Check(DealAlertPolicy.DiscountPercent(0, "MXN", 0) is null, "discount zero base null");
        Check(DealAlertPolicy.DiscountPercent(-100, "MXN", 0) is null, "discount negative base null");
        Check(DealAlertPolicy.DiscountPercent(10_000, "MXN", null) is null, "discount missing best null");

        CheckEqual(7d, DealAlertPolicy.DealScore(10_000, "MXN", 1_000, null, null, 50), "score 7 at 90%");
        CheckEqual(0d, DealAlertPolicy.DealScore(10_000, "MXN", 6_000, null, null, 50), "score below threshold");
        CheckEqual(0d, DealAlertPolicy.DealScore(10_000, "MXN", 5_000, null, null, 50), "score at threshold");
        CheckEqual(10d, DealAlertPolicy.DealScore(10_000, "MXN", 1_000, 1_000, "MXN", 50), "score 10 at low");
        CheckEqual(10d, DealAlertPolicy.DealScore(10_000, "MXN", 0, 1_000, "MXN", 50), "score 10 free");
        CheckEqual(8.5d, DealAlertPolicy.DealScore(10_000, "MXN", 1_000, 500, "MXN", 50), "score 8.5 half low");
        CheckEqual(3d, DealAlertPolicy.DealScore(10_000, "MXN", 6_000, 6_000, "MXN", 50), "score 3 below threshold low");
        CheckEqual(7d, DealAlertPolicy.DealScore(10_000, "MXN", 1_000, 1_000, "USD", 50), "score foreign low no bonus");
        CheckEqual(0d, DealAlertPolicy.DealScore(10_000, "MXN", 1_500, null, null, 95), "score threshold above ceiling");
        CheckEqual(7d, DealAlertPolicy.DealScore(10_000, "MXN", 400, null, null, 95), "score full at 95");
        Check(DealAlertPolicy.DealScore(10_000, "USD", 5_000, null, null, 50) is null, "score non-MXN null");
        Check(DealAlertPolicy.DealScore(10_000, "MXN", null, null, null, 50) is null, "score missing best null");
    }

    /// <summary>Strict &lt; 2000 minor and strict &gt; 7 boundaries.</summary>
    private static void ThresholdBoundaries()
    {
        // 98% discount gives exactly 7.0, so price is the only possible reason. 2000 does not fire.
        var atBoundary = DealAlertPolicy.Evaluate(Candidate(price: 2_000, basePrice: 100_000), Context);
        Check(!atBoundary.Triggered, "price exactly 2000 does not fire");
        CheckEqual(7d, atBoundary.Score, "price boundary score is 7.0");

        var belowBoundary = DealAlertPolicy.Evaluate(Candidate(price: 1_999, basePrice: 100_000), Context);
        Check(belowBoundary.Triggered, "price 1999 fires");
        Check(belowBoundary.Reasons.SequenceEqual(new[] { DealAlertReasons.Price }), "price 1999 fires by price only");

        // 90% discount == 7.0 exactly; a history bonus lifts it over 7.
        var scoreAtBoundary = DealAlertPolicy.Evaluate(Candidate(price: 2_000, basePrice: 20_000), Context);
        Check(!scoreAtBoundary.Triggered, "score exactly 7.0 does not fire");
        CheckEqual(7d, scoreAtBoundary.Score, "score boundary is 7.0");

        // Same price, history low 1000 -> lowFactor 0.5 -> 8.5.
        var scoreAbove = DealAlertPolicy.Evaluate(
            Candidate(price: 2_000, basePrice: 20_000, mxnLow: 1_000, mxnLowCurrency: "MXN"), Context);
        Check(scoreAbove.Triggered, "score 8.5 fires");
        Check(scoreAbove.Score > 7d, "score strictly greater than 7");
        Check(scoreAbove.Reasons.Contains(DealAlertReasons.Score), "score reason present");
    }

    private static void OrRules()
    {
        var priceOnly = DealAlertPolicy.Evaluate(Candidate(price: 1_999, basePrice: 100_000), Context);
        Check(priceOnly.Reasons.SequenceEqual(new[] { DealAlertReasons.Price }), "OR: cheap price alone");

        var scoreOnly = DealAlertPolicy.Evaluate(
            Candidate(price: 2_000, basePrice: 20_000, mxnLow: 1_000, mxnLowCurrency: "MXN"), Context);
        Check(scoreOnly.Reasons.Contains(DealAlertReasons.Score) && scoreOnly.Reasons.Count == 1, "OR: score alone");

        var historyOnly = DealAlertPolicy.Evaluate(
            Candidate(price: 3_000, basePrice: 10_000, historyLow: 3_000, historyCurrency: "MXN",
                mxnLow: 3_000, mxnLowCurrency: "MXN"),
            Context);
        Check(historyOnly.Triggered, "OR: history low alone fires");
        CheckEqual(6.5d, historyOnly.Score, "OR: history-only score stays under 7");
        Check(historyOnly.Reasons.SequenceEqual(new[] { DealAlertReasons.HistoryLow }), "OR: history reason only");

        var allThree = DealAlertPolicy.Evaluate(
            Candidate(price: 1_000, basePrice: 20_000, historyLow: 1_000, historyCurrency: "MXN", mxnLow: 100, mxnLowCurrency: "MXN"),
            Context);
        Check(allThree.Reasons.SequenceEqual(new[] { DealAlertReasons.Price, DealAlertReasons.Score, DealAlertReasons.HistoryLow }),
            "OR: all three reasons in stable order");
    }

    private static void HistoryLowEvidence()
    {
        Check(DealAlertPolicy.HasVerifiedHistoryLow(
            Candidate(price: 3_000, historyLow: 3_000, historyCurrency: "MXN")), "history same currency current==low");
        Check(DealAlertPolicy.HasVerifiedHistoryLow(
            Candidate(price: 3_000, historyLow: 5_000, historyCurrency: "MXN")), "history current below low");
        Check(!DealAlertPolicy.HasVerifiedHistoryLow(
            Candidate(price: 3_000, historyLow: 3_000, historyCurrency: "USD", originalCurrency: "MXN")),
            "history foreign currency rejected");
        Check(!DealAlertPolicy.HasVerifiedHistoryLow(
            Candidate(price: 3_000, historyLow: 2_000, historyCurrency: "MXN")), "history above current rejected");
        Check(!DealAlertPolicy.HasVerifiedHistoryLow(
            Candidate(price: 3_000, historyLow: null, historyCurrency: null)), "missing history rejected");
        Check(!DealAlertPolicy.HasVerifiedHistoryLow(
            Candidate(price: 3_000, historyLow: 0, historyCurrency: "MXN")), "zero history rejected");
        Check(!DealAlertPolicy.HasVerifiedHistoryLow(
            Candidate(price: 3_000, historyLow: 3_000, historyCurrency: "MXN") with { OriginalCurrentPriceMinor = null }),
            "missing original price rejected");
        Check(!DealAlertPolicy.HasVerifiedHistoryLow(
            Candidate(source: "steam", price: 3_000, historyLow: 3_000, historyCurrency: "MXN")),
            "self-observed Steam low is not evidence");
    }

    private static void InvalidStaleAndCurrency()
    {
        Check(!DealAlertPolicy.Evaluate(Candidate(price: 0), Context).Triggered, "zero price rejected");
        Check(!DealAlertPolicy.Evaluate(Candidate(price: -1), Context).Triggered, "negative price rejected");
        Check(!DealAlertPolicy.Evaluate(Candidate(price: DealAlertPolicy.MaxPriceMinor + 1), Context).Triggered,
            "absurd price rejected");
        Check(!DealAlertPolicy.Evaluate(Candidate(pricing: "unconverted"), Context).Triggered,
            "unconverted pricing rejected");
        Check(!DealAlertPolicy.Evaluate(
            Candidate(price: 1_999, observedAt: Now - TimeSpan.FromHours(73)), Context).Triggered,
            "stale snapshot rejected even when cheap");
        Check(!DealAlertPolicy.Evaluate(
            Candidate(price: 1_999, observedAt: Now + TimeSpan.FromSeconds(1)), Context).Triggered,
            "future snapshot rejected even when cheap");

        // Epic's deferred free rows are refused, but a paid Epic offer is not.
        Check(!DealAlertPolicy.Evaluate(Candidate(source: "epic", price: 0, originalPrice: 0), Context).Triggered,
            "epic free rejected");
        Check(!DealAlertPolicy.Evaluate(
            Candidate(source: "epic", price: 1_500) with { OriginalCurrentPriceMinor = null }, Context).Triggered,
            "epic questionable offer without original price rejected");
        var epicPaid = DealAlertPolicy.Evaluate(Candidate(source: "epic", price: 1_999), Context);
        Check(epicPaid.Triggered && epicPaid.Reasons.Contains(DealAlertReasons.Price), "epic paid still fires by price");

        // A non-MXN Steam base kills the score but not the other rules.
        var nonMxnBase = DealAlertPolicy.Evaluate(Candidate(price: 1_999, baseCurrency: "USD"), Context);
        Check(nonMxnBase.Triggered && nonMxnBase.Score is null, "non-MXN base: score null, price still fires");
        Check(!DealAlertPolicy.Evaluate(Candidate(price: 5_000, baseCurrency: "USD"), Context).Triggered,
            "non-MXN base with no other rule does not fire");
    }

    private static void DedupIdentity()
    {
        var a = Candidate(price: 1_500, offerKey: "abc");
        var b = Candidate(price: 1_500, offerKey: "abc");
        var c = Candidate(price: 1_400, offerKey: "abc");
        var d = Candidate(price: 1_500, offerKey: "xyz");
        CheckEqual(DealAlertPolicy.DedupKey(a), DealAlertPolicy.DedupKey(b), "same offer same key");
        Check(DealAlertPolicy.DedupKey(a) != DealAlertPolicy.DedupKey(c), "price change changes key");
        Check(DealAlertPolicy.DedupKey(a) != DealAlertPolicy.DedupKey(d), "offer change changes key");
        Check(DealAlertPolicy.DedupKey(a).StartsWith($"v{DealAlertPolicy.TriggerVersion}|", StringComparison.Ordinal),
            "key carries the trigger version");
        Check(DealAlertPolicy.DedupKey(a).Contains("|itad|", StringComparison.Ordinal), "key carries source");
        Check(DealAlertPolicy.DedupKey(a).Contains("|1500", StringComparison.Ordinal), "key carries price");
    }

    private static void MessagesAndBatch()
    {
        var decision = DealAlertPolicy.Evaluate(
            Candidate(price: 1_500, shop: "Steam", dealUrl: "https://store.steampowered.com/app/1/"), Context);
        Check(decision.Message is not null, "firing decision has a message");
        Check(decision.Message!.Title.Contains("Floppy Knights", StringComparison.Ordinal), "title has game name");
        Check(decision.Message.Title.Contains("MX$15.00", StringComparison.Ordinal), "title has price");
        Check(decision.Message.Body.Contains("Steam", StringComparison.Ordinal), "body has shop");
        CheckEqual("https://store.steampowered.com/app/1/", decision.Message.ClickUrl, "click url preserved");

        var rejected = DealAlertPolicy.Evaluate(Candidate(price: 5_000), Context);
        Check(rejected.Message is null, "rejected decision has no message");

        var batch = DealAlertPolicy.EvaluateAll(
            [Candidate(price: 1_999), Candidate(price: 5_000), Candidate(price: 1_000)],
            Context);
        CheckEqual(2, batch.Count, "batch keeps only firing candidates");
        CheckEqual(DealAlertPolicy.DedupKey(batch[0].Candidate), DealAlertPolicy.DedupKey(Candidate(price: 1_999)),
            "batch preserves input order");
    }

    private static void ProducerRejectsBeforeEnqueue()
    {
        var reader = new FakeCandidateReader
        {
            Candidates =
            {
                Candidate(price: 1_999, offerKey: "cheap"),
                Candidate(price: 9_999, offerKey: "rejected"),
                Candidate(price: 2_000, basePrice: 20_000, mxnLow: 1_000, mxnLowCurrency: "MXN", offerKey: "scored"),
            },
        };
        var outbox = new FakeOutbox();
        var clock = new FixedTimeProvider(Now);
        var producer = new DealAlertProducer(reader, outbox, Options(50), clock);

        var report = producer.ProduceAsync(CancellationToken.None).GetAwaiter().GetResult();

        CheckEqual(3, report.Candidates, "producer saw three candidates");
        CheckEqual(2, report.Triggered, "producer triggered two");
        CheckEqual(1, outbox.EnqueueCalls, "producer enqueues once");
        CheckEqual(2, outbox.LastDecisions.Count, "only firing decisions reach the outbox");
        Check(!outbox.LastDecisions.Any(d => d.Candidate.OfferKey == "rejected"),
            "rejected candidate never reaches the outbox");

        // All-rejected batch must still not mutate anything: TryEnqueueAsync receives an empty list.
        var readerEmpty = new FakeCandidateReader { Candidates = { Candidate(price: 9_999) } };
        var outboxEmpty = new FakeOutbox();
        var producerEmpty = new DealAlertProducer(readerEmpty, outboxEmpty, Options(50), new FixedTimeProvider(Now));
        var emptyReport = producerEmpty.ProduceAsync(CancellationToken.None).GetAwaiter().GetResult();
        CheckEqual(0, emptyReport.Triggered, "all-rejected batch triggers nothing");
        CheckEqual(0, outboxEmpty.LastDecisions.Count, "all-rejected batch enqueues nothing");
        CheckEqual(0, outboxEmpty.Rows.Count, "all-rejected batch leaves the outbox untouched");
    }

    private static Microsoft.Extensions.Options.IOptions<Deals.API.NtfyOptions> Options(int threshold) =>
        Microsoft.Extensions.Options.Options.Create(new Deals.API.NtfyOptions
        {
            MinViableDiscountPercent = threshold,
            MaxSnapshotAgeHours = 72,
        });
}
