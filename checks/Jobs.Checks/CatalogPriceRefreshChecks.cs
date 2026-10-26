#nullable enable
using System.Text.Json;
using Deals.API.Extensions;
using Deals.API.Services.Jobs;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobsChecks;

/// <summary>
/// Behaviour checks for the manual-only <c>catalog.prices-refresh</c> job (spec S5/S6):
/// <list type="number">
/// <item>the job is in the catalog, outside automatic scheduling and bounded to the documented batch;</item>
/// <item>one refresh attempt is classified from its typed phase outcome, so a degraded provider answer can
/// never be reported as a successful price write, and an authoritative "nothing comparable" is not an
/// error;</item>
/// <item>the executor turns the counters into one honest result — degraded or unsupported runs are never
/// <c>ok</c>, and a run that selected nothing names why;</item>
/// <item>no host service schedules it: it only runs because a human queued it.</item>
/// </list>
/// The pass itself needs a database, so it is not executed here; the classification seam is pure and is
/// exercised directly, and the selection/policy rules are pinned by the catalog and game-sync checks.
/// </summary>
internal static class CatalogPriceRefreshChecks
{
    public static void Run()
    {
        CatalogEntry();
        AttemptClassification();
        ExecutorCounters();
        RequesterIsTrusted();
        RequesterBindingThroughSubmission();
        ManualOnly();
        Console.WriteLine("PASS: catalog price refresh job checks");
    }

    private static void CatalogEntry()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);
        var entry = catalog.Find(JobKeys.CatalogPricesRefresh);

        Check(entry is not null, "the catalog exposes catalog.prices-refresh");
        Check(entry!.Enabled, "the manual price pass is available");
        Check(!entry.Destructive, "the price pass deletes nothing");
        Check(!entry.RequiresConfirmation, "re-pricing persisted prices needs no confirmation");
        Check(entry.Options.Count == 0, "the price pass has no options to invent");

        // The batch bound is a constant, not an option: an operator cannot ask for the whole-catalog sweep.
        Check(CatalogPriceRefreshLimits.BatchSize == 20, "the batch is hard bounded to 20 games");
        Check(
            !entry.Options.Any(option => option.Key.Contains("batch", StringComparison.OrdinalIgnoreCase)),
            "there is no option that widens the batch");

        // Manual-only: the schedule text has to say so, because the catalog is the operator's only source.
        Check(
            entry.Schedule.Contains("manual", StringComparison.OrdinalIgnoreCase),
            "the price pass is documented as manual-only");
        Check(
            !entry.Schedule.Contains("cada", StringComparison.OrdinalIgnoreCase),
            "the price pass declares no recurring schedule");

        // Distinct lock: a manual run must not share a session key with any other job. The key serialises
        // *this* job (two manual requests, or a second instance), not the other jobs.
        Check(entry.AdvisoryLockKey is not null, "the price pass declares an advisory lock");
        Check(
            catalog.All.Count(other => other.AdvisoryLockKey == entry.AdvisoryLockKey) == 1,
            "the price pass lock key is unique in the catalog");
        Check(
            entry.AdvisoryLockKey == CatalogPriceRefreshJobExecutor.AdvisoryLockKey,
            "the catalog and the executor agree on the lock");

        // Spec S4/S5: the pass acts on the requester's wishlist, so it declares that it needs one and its
        // visible copy says so — no catalogue-wide promise and no «frescura» as a word.
        Check(entry.RequiresRequester, "the price pass declares that it needs an authenticated requester");
        Check(
            catalog.All.Count(other => other.RequiresRequester) == 1,
            "only the personal pending-price pass requires a requester");
        var copy = $"{entry.DisplayName} {entry.Description}";
        Check(
            copy.Contains("wishlist", StringComparison.OrdinalIgnoreCase),
            "the job copy names the wishlist that it prices");
        Check(
            !copy.Contains("frescura", StringComparison.OrdinalIgnoreCase),
            "the job copy does not use «frescura» as a word");
        Check(
            !copy.Contains("catálogo", StringComparison.OrdinalIgnoreCase) &&
            !copy.Contains("catalogo", StringComparison.OrdinalIgnoreCase),
            "the job copy no longer advertises the whole catalog");
        Check(
            CatalogPriceRefreshJobExecutor.JobKey == JobKeys.CatalogPricesRefresh,
            "the executor declares the catalog key");
    }

    /// <summary>
    /// The classification seam: (typed phase outcome, did the game end with a usable price, did that price
    /// advance) becomes exactly one verdict. This is where "the pipeline absorbed a provider fault" stops
    /// being invisible.
    /// </summary>
    private static void AttemptClassification()
    {
        // Authoritative answers, nothing degraded.
        var answered = Attempt(ran: true, advanced: ["itad", "ggdeals"]);

        CheckEqual(
            CatalogPriceRefreshVerdict.Updated,
            CatalogPriceRefreshClassification.Classify(answered, hasUsablePriceAfter: true, advanced: true),
            "an authoritative answer that advanced the price counts as an update");

        CheckEqual(
            CatalogPriceRefreshVerdict.Skipped,
            CatalogPriceRefreshClassification.Classify(answered, hasUsablePriceAfter: true, advanced: false),
            "an authoritative answer that changed nothing is a skip, not an update");

        CheckEqual(
            CatalogPriceRefreshVerdict.NoComparablePrice,
            CatalogPriceRefreshClassification.Classify(answered, hasUsablePriceAfter: false, advanced: false),
            "an authoritative no-match with no usable price is not an error");

        CheckEqual(
            CatalogPriceRefreshVerdict.Skipped,
            CatalogPriceRefreshClassification.Classify(
                SteamStalePriceRefresh.Declined(),
                hasUsablePriceAfter: true,
                advanced: false),
            "a declined gate (no snapshot any more) is a skip");

        // The pipeline absorbs a provider fault into a Failed phase result and still returns details. The
        // classification must not mistake that for a successful write.
        var degraded = Attempt(ran: true, advanced: [], degraded: ["epic"]);
        CheckEqual(
            CatalogPriceRefreshVerdict.Degraded,
            CatalogPriceRefreshClassification.Classify(degraded, hasUsablePriceAfter: true, advanced: false),
            "a degraded provider answer is never a successful write");

        CheckEqual(
            CatalogPriceRefreshVerdict.Degraded,
            CatalogPriceRefreshClassification.Classify(degraded, hasUsablePriceAfter: false, advanced: false),
            "a degraded provider answer is not an authoritative no-match either");

        // Partial success: one phase wrote a price, another failed. The run as a whole is degraded, and the
        // counters must not report it as clean.
        var partial = Attempt(ran: true, advanced: ["itad"], degraded: ["microsoft"]);
        CheckEqual(
            CatalogPriceRefreshVerdict.Degraded,
            CatalogPriceRefreshClassification.Classify(partial, hasUsablePriceAfter: true, advanced: true),
            "a partial success plus a failure is degraded");

        // Steam is a phase like the others: a stale Steam price that could not be re-fetched is degraded.
        var steamOnly = Attempt(ran: true, advanced: [], degraded: ["steam"], steamDue: true);
        CheckEqual(
            CatalogPriceRefreshVerdict.Degraded,
            CatalogPriceRefreshClassification.Classify(steamOnly, hasUsablePriceAfter: true, advanced: false),
            "a failed Steam phase on a stale Steam price is degraded");

        var steamRepriced = Attempt(ran: true, advanced: ["steam"], steamDue: true);
        CheckEqual(
            CatalogPriceRefreshVerdict.Updated,
            CatalogPriceRefreshClassification.Classify(steamRepriced, hasUsablePriceAfter: true, advanced: true),
            "a Steam direct price is a real update too");

        // The rule behind DegradedPhases: a real answer always writes its own timestamp.
        var earlier = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        Check(
            !SteamStalePriceRefresh.PersistedAnswer(failed: false, earlier, earlier),
            "a due phase that left its stamp untouched is not an authoritative answer");
        Check(
            !SteamStalePriceRefresh.PersistedAnswer(failed: true, earlier, later),
            "a failed phase is never an answer, even if some stamp moved");
        Check(
            SteamStalePriceRefresh.PersistedAnswer(failed: false, before: null, later),
            "a first answer advances a null stamp");
        Check(
            SteamStalePriceRefresh.PersistedAnswer(failed: false, earlier, later),
            "a newer stamp is an answer");

        // S9 (user correction «El precio más barato guardado»): "did this run update the price" is measured on
        // the same winner the monitor buckets by — the cheapest stored price. A phase that renews a more
        // expensive snapshot does not move that winner, and must not be counted as a price write.
        Check(
            CatalogPriceRefreshMeasurement.Advanced(
                beforePriceMinor: 3_000, beforeObservedAt: earlier,
                afterPriceMinor: 3_000, afterObservedAt: later),
            "the same cheap amount with a new observation date is an update");
        Check(
            CatalogPriceRefreshMeasurement.Advanced(
                beforePriceMinor: 3_000, beforeObservedAt: earlier,
                afterPriceMinor: 4_500, afterObservedAt: later),
            "a changed best price is an update, even when it went up");
        Check(
            !CatalogPriceRefreshMeasurement.Advanced(
                beforePriceMinor: 3_000, beforeObservedAt: earlier,
                afterPriceMinor: 3_000, afterObservedAt: earlier),
            "an identical winner is not an update");
        Check(
            CatalogPriceRefreshMeasurement.Advanced(
                beforePriceMinor: null, beforeObservedAt: null,
                afterPriceMinor: 3_000, afterObservedAt: later),
            "a first usable price is an update");
        Check(
            !CatalogPriceRefreshMeasurement.Advanced(
                beforePriceMinor: 3_000, beforeObservedAt: earlier,
                afterPriceMinor: null, afterObservedAt: null),
            "losing the price is not an update, it is a no-comparable-price answer");
    }

    private static void ExecutorCounters()
    {
        // A healthy bounded batch: every counter is distinct and every one reaches the details.
        var healthy = new CatalogPriceRefreshOutcome(
            Pending: 40,
            Selected: 20,
            Updated: 9,
            Skipped: 6,
            GateDeclined: 1,
            NoComparablePrice: 4,
            Unsupported: 3,
            NotDue: 17,
            Degraded: 0,
            Errors: 0,
            Remaining: 31);
        var ok = Run(new FakeCatalogPriceRefreshService(healthy));
        Check(ok.Succeeded, "a pass with no errors and no degraded phases is recorded as ok");
        var details = Details(ok);
        CheckEqual(40, details.GetProperty("pending").GetInt32(), "pending is reported");
        CheckEqual(20, details.GetProperty("selected").GetInt32(), "selected is reported");
        CheckEqual(9, details.GetProperty("updated").GetInt32(), "actual price updates are reported");
        CheckEqual(6, details.GetProperty("skipped").GetInt32(), "skips are reported");
        CheckEqual(1, details.GetProperty("gateDeclined").GetInt32(), "declined gates are reported");
        CheckEqual(4, details.GetProperty("noComparablePrice").GetInt32(), "no-comparable-price is reported");
        CheckEqual(3, details.GetProperty("unsupported").GetInt32(), "unsupported is reported");
        CheckEqual(17, details.GetProperty("notDue").GetInt32(), "cooled-down pending games are reported");
        CheckEqual(0, details.GetProperty("degraded").GetInt32(), "degraded phases are reported");
        CheckEqual(0, details.GetProperty("errors").GetInt32(), "errors are reported");
        CheckEqual(31, details.GetProperty("remaining").GetInt32(), "remaining is reported");
        CheckEqual(7, details.GetProperty("requestedBy").GetInt32(), "the outcome records whose wishlist it priced");
        CheckEqual(
            JsonValueKind.Null,
            details.GetProperty("reason").ValueKind,
            "a productive run needs no excuse");

        // One thrown exception degrades the run: it is never recorded as ok.
        var thrown = Run(new FakeCatalogPriceRefreshService(healthy with { Errors = 2 }));
        Check(!thrown.Succeeded, "any error marks the run degraded");
        CheckEqual(2, Details(thrown).GetProperty("errors").GetInt32(), "the error count survives");

        // A provider that answered "failed" is absorbed by the existing pipeline: without the typed outcome
        // this run used to look clean. It must fail.
        var degraded = Run(new FakeCatalogPriceRefreshService(healthy with { Degraded = 3 }));
        Check(!degraded.Succeeded, "degraded provider answers fail the run");
        CheckEqual(3, Details(degraded).GetProperty("degraded").GetInt32(), "the degraded count survives");

        var partial = Run(new FakeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            Pending: 40, Selected: 20, Updated: 9, Skipped: 0, GateDeclined: 0, NoComparablePrice: 0,
            Unsupported: 0, NotDue: 11, Degraded: 4, Errors: 0, Remaining: 31)));
        Check(!partial.Succeeded, "a partial pass with some failures is not ok");
        CheckEqual(9, Details(partial).GetProperty("updated").GetInt32(), "its real updates are still reported");

        // Pending and entirely unpricedable: surfaced as failed with the reason, not as a silent ok.
        var unsupportedOnly = Run(new FakeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            Pending: 5, Selected: 0, Updated: 0, Skipped: 0, GateDeclined: 0, NoComparablePrice: 0,
            Unsupported: 5, NotDue: 0, Degraded: 0, Errors: 0, Remaining: 5)));
        Check(!unsupportedOnly.Succeeded, "a pending set with nothing priceable is not a success");
        CheckEqual(
            "pending_unsupported_only",
            Details(unsupportedOnly).GetProperty("reason").GetString(),
            "the unsupported limitation is named");

        // Every pending game is cooled down: nothing to do, and the run says exactly that.
        var nothingDue = Run(new FakeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            Pending: 12, Selected: 0, Updated: 0, Skipped: 0, GateDeclined: 0, NoComparablePrice: 0,
            Unsupported: 0, NotDue: 12, Degraded: 0, Errors: 0, Remaining: 12)));
        Check(nothingDue.Succeeded, "a pass with no due phase is not a failure");
        CheckEqual(
            "no_provider_due",
            Details(nothingDue).GetProperty("reason").GetString(),
            "the reason is named instead of claiming work");

        // A mixed pending set (some unpricedable, some cooled down) must not claim to be unsupported-only.
        var mixed = Run(new FakeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            Pending: 9, Selected: 0, Updated: 0, Skipped: 0, GateDeclined: 0, NoComparablePrice: 0,
            Unsupported: 3, NotDue: 6, Degraded: 0, Errors: 0, Remaining: 9)));
        CheckEqual(
            "pending_unsupported_and_not_due",
            Details(mixed).GetProperty("reason").GetString(),
            "a mixed pending set names both limitations");
        Check(mixed.Succeeded, "a mixed set with nothing due and nothing priceable is not a failure");

        // Nothing pending at all: the catalog is covered.
        var covered = Run(new FakeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            Pending: 0, Selected: 0, Updated: 0, Skipped: 0, GateDeclined: 0, NoComparablePrice: 0,
            Unsupported: 0, NotDue: 0, Degraded: 0, Errors: 0, Remaining: 0)));
        Check(covered.Succeeded, "a covered catalog is a successful no-op");
        CheckEqual(
            "nothing_pending",
            Details(covered).GetProperty("reason").GetString(),
            "the empty run says it was empty");

        // A wishlist whose entries could not be attributed is not a covered wishlist: the run reports the count
        // and refuses to call a zero selection "nothing pending".
        var unresolvedOnly = Run(new FakeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            Pending: 0, Selected: 0, Updated: 0, Skipped: 0, GateDeclined: 0, NoComparablePrice: 0,
            Unsupported: 0, NotDue: 0, Degraded: 0, Errors: 0, Remaining: 0, UnresolvedWishlistEntries: 3)));
        CheckEqual(
            "nothing_pending_unresolved_entries",
            Details(unresolvedOnly).GetProperty("reason").GetString(),
            "unresolved wishlist entries stop a zero selection from claiming nothing is pending");
        CheckEqual(3, Details(unresolvedOnly).GetProperty("unresolvedWishlistEntries").GetInt32(),
            "the unresolved entry count is part of the job record");
        Check(unresolvedOnly.Succeeded, "nothing failed, so the run is not failed for a limitation it reports");

        // PRESERVE: with no unresolved entries the empty reason is exactly what it was.
        CheckEqual(
            "nothing_pending",
            Details(covered).GetProperty("reason").GetString(),
            "a fully readable and covered wishlist still says nothing_pending");
        CheckEqual(0, Details(covered).GetProperty("unresolvedWishlistEntries").GetInt32(),
            "a covered wishlist reports zero unresolved entries");

        // Unresolved entries next to real work do not change the honest counters of that work.
        var mixedUnresolved = Run(new FakeCatalogPriceRefreshService(healthy with { UnresolvedWishlistEntries = 2 }));
        CheckEqual(9, Details(mixedUnresolved).GetProperty("updated").GetInt32(),
            "resolved work is still reported as an update");
        CheckEqual(2, Details(mixedUnresolved).GetProperty("unresolvedWishlistEntries").GetInt32(),
            "unresolved entries travel next to the canonical counters");

        // A partial batch is not a failure: remaining is how the operator sees that there is more to do.
        var bounded = Run(new FakeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            Pending: 100, Selected: 20, Updated: 14, Skipped: 6, GateDeclined: 0, NoComparablePrice: 0,
            Unsupported: 0, NotDue: 40, Degraded: 0, Errors: 0, Remaining: 80)));
        Check(bounded.Succeeded, "a bounded batch that finished its selected games is ok");
        CheckEqual(80, Details(bounded).GetProperty("remaining").GetInt32(), "the rest is reported as remaining");
    }

    /// <summary>
    /// Spec S5: the batch belongs to the person who asked for it. The identity comes from the authenticated
    /// request (never from the payload) and reaches execution through the queue item; when it is absent the
    /// pass must refuse instead of falling back to the old catalogue-wide sweep or to a configured account.
    /// </summary>
    private static void RequesterIsTrusted()
    {
        // (a) Called without a requester, the executor refuses and never starts the pass: no provider budget
        // is spent on a batch whose owner is unknown.
        var direct = new RecordingCatalogPriceRefreshService();
        var refused = new CatalogPriceRefreshJobExecutor(direct)
            .ExecuteAsync(JobRunOptions.Empty, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Check(!refused.Succeeded, "a run without a trusted requester is refused");
        CheckEqual(0, direct.Calls, "a run without a requester makes no provider call");
        CheckEqual(
            "requester_required",
            Details(refused).GetProperty("reason").GetString(),
            "the refusal names the missing requester");

        // (b) The same thing through the dispatcher: a queued item with no requester binding is closed as
        // failed by the runner and the executor never runs.
        var queued = new RecordingCatalogPriceRefreshService();
        var tracker = new RecordingTracker();
        var runner = new JobExecutionRunner(
            new JobCatalog(JobCatalogSettings.Default),
            [new CatalogPriceRefreshJobExecutor(queued)],
            tracker,
            NullLogger<JobExecutionRunner>.Instance);

        runner.RunAsync(
                new JobQueueItem(JobKeys.CatalogPricesRefresh, 601, JobRunOptions.Empty),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        CheckEqual(0, queued.Calls, "an unbound queued run never reaches the pass");
        CheckEqual(1, tracker.Finished.Count, "an unbound queued run still closes its row");
        CheckEqual(JobRunStatuses.Failed, tracker.Finished[0].Status, "an unbound queued run is recorded as failed");
        Check(
            tracker.Finished[0].Details?.Contains("requester_required", StringComparison.Ordinal) == true,
            "an unbound queued run records why it was refused");

        // (c) Positive control: the requester captured when the run was queued is what the pass receives, and
        // the closing record keeps it. Without this control the two refusals above would pass on a broken wire.
        var trusted = new RecordingCatalogPriceRefreshService();
        var trustedTracker = new RecordingTracker();
        var trustedRunner = new JobExecutionRunner(
            new JobCatalog(JobCatalogSettings.Default),
            [new CatalogPriceRefreshJobExecutor(trusted)],
            trustedTracker,
            NullLogger<JobExecutionRunner>.Instance);

        trustedRunner.RunAsync(
                new JobQueueItem(JobKeys.CatalogPricesRefresh, 602, JobRunOptions.Empty, RequestedByUserId: 42),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        CheckEqual("42", string.Join(",", trusted.Requesters), "the queued requester reaches the pass");
        CheckEqual(JobRunStatuses.Ok, trustedTracker.Finished[0].Status, "a bound run with no faults is ok");
        Check(
            trustedTracker.Finished[0].Details?.Contains("\"requestedBy\":42", StringComparison.Ordinal) == true,
            "the executed run records whose wishlist it priced");
    }

    /// <summary>
    /// The queue is where the identity is bound: a personal job is refused before anything is reserved or
    /// written when the request has no authenticated requester, and the accepted item carries the id so the
    /// background execution never has to look at a request context.
    /// </summary>
    private static void RequesterBindingThroughSubmission()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);
        var queue = new FakeJobRunQueue();
        var tracker = new RecordingTracker();
        var submissions = new JobSubmissionService(catalog, queue, tracker, NullLogger<JobSubmissionService>.Instance);

        var refused = submissions
            .SubmitAsync(JobKeys.CatalogPricesRefresh, null, false, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        CheckEqual(JobSubmitStatus.RequesterRequired, refused.Status,
            "a personal job asked for without a requester is refused");
        CheckEqual(0, queue.ReserveCalls, "the refusal reserves nothing");
        CheckEqual(0, tracker.Started.Count, "the refusal writes no run row");

        var accepted = submissions
            .SubmitAsync(JobKeys.CatalogPricesRefresh, null, false, CancellationToken.None, requestedByUserId: 42)
            .GetAwaiter()
            .GetResult();

        Check(accepted.IsSuccess, "a personal job with a requester is accepted");
        var item = queue.Published.Single();
        CheckEqual<int?>(42, item.RequestedByUserId, "the queued item carries the authenticated requester");
        Check(
            tracker.StartedDetails.Any(details =>
                details is not null && details.Contains("\"requestedBy\":42", StringComparison.Ordinal)),
            "the requester is persisted with the running row, not only held in memory");

        // The payload cannot name a user: an option called userId is an unknown key and is refused, so the
        // identity of the run can only come from the token.
        using var foreignUser = JsonDocument.Parse("""{"userId":1}""");
        var injected = submissions
            .SubmitAsync(
                JobKeys.CatalogPricesRefresh,
                foreignUser.RootElement,
                false,
                CancellationToken.None,
                requestedByUserId: 42)
            .GetAwaiter()
            .GetResult();

        CheckEqual(JobSubmitStatus.Invalid, injected.Status, "the payload cannot choose whose wishlist is priced");

        // Every other job keeps working exactly as before: no requester is required from a scheduled cycle.
        var untouched = submissions
            .SubmitAsync(JobKeys.FxRateRefresh, null, false, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Check(untouched.IsSuccess, "a job that is not personal still needs no requester");
        Check(queue.Published.Count == 2, "both accepted runs reached the queue");
        Check(queue.Published[1].RequestedByUserId is null,
            "a job that is not personal does not carry a wishlist owner");
    }

    private static void ManualOnly()
    {
        IServiceCollection services = new ServiceCollection();
        ServiceCollectionExtensions.AddApiApplicationServices(services);

        // Nothing in the host schedules this pass: it is not a hosted service, and the only registrations are
        // the scoped executor and its IJobExecutor alias.
        var hosted = services
            .Where(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(descriptor => descriptor.ImplementationType?.Name ?? string.Empty)
            .ToList();
        Check(
            !hosted.Any(name => name.Contains("CatalogPrice", StringComparison.Ordinal)),
            "no hosted service schedules the manual price pass");

        Check(
            services.Any(descriptor =>
                descriptor.ServiceType == typeof(CatalogPriceRefreshJobExecutor) &&
                descriptor.Lifetime == ServiceLifetime.Scoped),
            "the executor is registered scoped");

        // The pass reads persisted prices and writes real provider answers: it must never be reachable from a
        // scheduled cycle, and its only trigger is the manual dispatch queue.
        Check(
            services.All(descriptor =>
                descriptor.ImplementationType != typeof(CatalogPriceRefreshService) ||
                descriptor.ServiceType == typeof(ICatalogPriceRefreshService)),
            "the pass is exposed only through its interface");
    }

    private static SteamStalePriceRefresh Attempt(
        bool ran,
        string[] advanced,
        string[]? degraded = null,
        bool steamDue = false) =>
        new(
            ran,
            steamDue,
            DuePhases: [SteamStalePricePhases.Steam, SteamStalePricePhases.Itad],
            AdvancedPhases: advanced,
            DegradedPhases: degraded ?? []);

    /// <summary>
    /// Runs the executor the way the dispatcher does: with the authenticated requester that was captured when the
    /// job was queued.
    /// </summary>
    private static JobExecutionResult Run(ICatalogPriceRefreshService service, int requestedByUserId = 7) =>
        new CatalogPriceRefreshJobExecutor(service)
            .ExecuteAsync(JobRunOptions.Empty, requestedByUserId, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static JsonElement Details(JobExecutionResult result)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(result.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return document.RootElement.Clone();
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }

    private static void CheckEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{name}: expected {expected}, got {actual}");
    }

    /// <summary>Canned pass outcome: the executor mapping is what is under test, not the pass itself.</summary>
    private sealed class FakeCatalogPriceRefreshService(CatalogPriceRefreshOutcome outcome)
        : ICatalogPriceRefreshService
    {
        public Task<CatalogPriceRefreshOutcome> RefreshPendingAsync(
            int requestedByUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(outcome);
    }

    /// <summary>
    /// Pass double that also records how often it ran, so "refused before any provider call" is observable
    /// instead of assumed.
    /// </summary>
    private sealed class RecordingCatalogPriceRefreshService
        : ICatalogPriceRefreshService
    {
        public int Calls { get; private set; }

        /// <summary>The requester each call received, so "whose list" is observable too.</summary>
        public List<int> Requesters { get; } = [];

        public Task<CatalogPriceRefreshOutcome> RefreshPendingAsync(
            int requestedByUserId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Requesters.Add(requestedByUserId);
            return Task.FromResult(new CatalogPriceRefreshOutcome(
                Pending: 1, Selected: 1, Updated: 1, Skipped: 0, GateDeclined: 0, NoComparablePrice: 0,
                Unsupported: 0, NotDue: 0, Degraded: 0, Errors: 0, Remaining: 0));
        }
    }
}
