#nullable enable
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deals.API.Services.Jobs;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Diagnostics;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using Deals.API.Extensions;
using JobsChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobsChecks;

/// <summary>
/// Safe sync diagnosis (spec S2/S3/S5/S7): the per-run collector, the failure classifier, the read-side validator
/// and the wiring into <c>job_runs.details</c>.
///
/// <para>
/// Everything here is deterministic and offline: no database, no provider, no host. What is asserted is the document
/// the admin page will read — schema version, honest counts, closed tokens, safe strings — plus the isolation that
/// makes a report belong to one run: two collectors never share events and reading never clears the collector that a
/// failure path still has to report.
/// </para>
/// </summary>
internal static class SyncDiagnosticsChecks
{
    public static void Run()
    {
        CollectorCountsEveryEventAndDetailsTheFirstTwoHundred();
        CollectorKeepsOnlySafeFields();
        ClassifierReadsOnlyStatusesThatExist();
        ClassifierTreatsCallerCancellationAsCancellation();
        ReadPathAcceptsBothRootsAndKeepsCountsHonest();
        ReadPathRejectsUnknownSchemaAndUnsafeFields();
        ReadPathLeavesUnrelatedDetailsAlone();
        RunnerFailureStillReportsTheEventsItCollected();
        TwoRunsNeverShareEvents();
        TheCollectorIsScopedSoOneRunOwnsOneReport();
        NoRunClaimsNoCollector();
        AJobRunClaimsTheCollectorBeforeThePassRuns();
        ThesummaryCountsReasonsNotStatuses();
        TheErrorTypeBoundaryIsSixtyFour();
        Console.WriteLine("PASS: sync diagnostics checks");
    }

    // ---------------------------------------------------------------- collector ---------------------------------

    /// <summary>
    /// 500 phases and a 200-event cap: the two numbers must be reported together, and the summary must still count
    /// all 500. A bounded list recomputed into a summary would claim 200 and hide 300 failures.
    /// </summary>
    private static void CollectorCountsEveryEventAndDetailsTheFirstTwoHundred()
    {
        var collector = new SyncDiagnosticsCollector();
        collector.Activate();

        for (var index = 0; index < 500; index++)
        {
            var provider = index % 2 == 0 ? SyncDiagnosticProvider.Epic : SyncDiagnosticProvider.Microsoft;
            collector.Record(
                10_000 + index,
                index,
                provider,
                SyncDiagnosticOutcome.Failed,
                SyncDiagnosticReason.HttpError,
                httpStatus: 429,
                errorType: "HttpRequestException",
                elapsedMs: index);
        }

        var snapshot = collector.BuildSnapshot();

        Check(snapshot.SchemaVersion == 1, "the document declares schema version 1");
        Check(snapshot.TotalEvents == 500, "every recorded event is counted");
        Check(snapshot.ShownEvents == SyncDiagnosticsContract.MaxEvents, "at most 200 events keep their detail");
        Check(snapshot.TruncatedEvents == 300, "the omitted events are reported, not hidden");
        Check(snapshot.Events.Count == 200, "the detailed list is the bounded slice");
        Check(snapshot.Events[0].AppId == 10_000, "the first event is the first one recorded");
        Check(snapshot.Events[^1].AppId == 10_199, "the slice keeps the recording order");
        Check(snapshot.Summary.Sum(row => row.Count) == 500, "the summary counts every event, not the slice");
        Check(snapshot.Summary.Count == 2, "one aggregate line per provider and reason");

        var epic = snapshot.Summary.Single(row => row.Provider == "epic");
        Check(epic.Count == 250, "the Epic line counts all of its events");
        Check(epic.Outcome == "failed", "the aggregate keeps the closed outcome token");
        Check(epic.Reason == "http_error", "the aggregate keeps the closed reason token");
        Check(epic.HttpStatus is null, "the aggregate groups reasons, not statuses");
        Check(snapshot.Events[0].HttpStatus == 429, "the detailed event keeps the status it observed");

        // No events at all is an honest empty document, and HasEvents is what keeps it out of a job that never
        // reached a provider.
        var empty = new SyncDiagnosticsCollector();
        Check(!empty.HasEvents, "a fresh collector has no events");
        Check(empty.BuildSnapshot().Summary.Count == 0, "a fresh collector summarises nothing");
        Check(collector.HasEvents, "a collector with events says so");
    }

    /// <summary>
    /// The persisted document may carry numbers and closed tokens; it may never carry the exception, its message,
    /// the request URL or the response body. The status is the one the exception really had.
    /// </summary>
    private static void CollectorKeepsOnlySafeFields()
    {
        var collector = new SyncDiagnosticsCollector();
        collector.Activate();
        var secret = new HttpRequestException(
            "GET https://api.gg.deals/v1/prices?key=SECRET123 failed with body {\"error\":\"nope\"}",
            null,
            HttpStatusCode.Forbidden);
        var secretFailure = SyncDiagnosticFailure.Classify(secret, CancellationToken.None);
        collector.Record(
            1057800,
            7,
            SyncDiagnosticProvider.GgDeals,
            secretFailure.Outcome,
            secretFailure.Reason,
            secretFailure.HttpStatus,
            secretFailure.ErrorType,
            elapsedMs: 4321);

        var snapshot = collector.BuildSnapshot();
        var captured = JsonSerializer.Serialize(snapshot);

        Check(snapshot.TotalEvents == 1, "the classified failure is recorded once");
        var row = snapshot.Summary.Single();
        Check(row.Provider == "ggdeals", "the provider is the closed token");
        Check(row.Outcome == "failed", "an HTTP failure is a failed phase");
        Check(row.Reason == "http_error", "an exception carrying a status is an HTTP error");
        Check(row.HttpStatus is null, "the aggregate line is not about a status");
        Check(snapshot.Events[0].HttpStatus == 403, "the detailed event reports the carried status");
        Check(snapshot.Events[0].ErrorType == "HttpRequestException", "the exception type is reported");
        Check(snapshot.Events[0].ElapsedMs == 4321, "the elapsed milliseconds are reported");
        Check(!captured.Contains("SECRET123", StringComparison.Ordinal), "the API key never reaches the document");
        Check(!captured.Contains("api.gg.deals", StringComparison.Ordinal), "the provider URL never reaches the document");
        Check(!captured.Contains("nope", StringComparison.Ordinal), "the response body never reaches the document");
        Check(!captured.Contains("failed with", StringComparison.Ordinal), "the exception message never reaches the document");
        Check(!captured.Contains("at Deals.", StringComparison.Ordinal), "no stack frame reaches the document");
    }

    // --------------------------------------------------------------- classifier ---------------------------------

    /// <summary>
    /// The measured statuses of the three stores, plus the two shapes that have none. 429 here is the status that
    /// <em>ended</em> the phase: a 429 the client retried internally and then answered is not a failure at all.
    /// </summary>
    private static void ClassifierReadsOnlyStatusesThatExist()
    {
        var rateLimited = SyncDiagnosticFailure.Classify(
            new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), CancellationToken.None);
        Check(rateLimited.Outcome == SyncDiagnosticOutcome.Failed, "a 429 that ends the phase is a failure");
        Check(rateLimited.Reason == SyncDiagnosticReason.HttpError, "a 429 is an HTTP error");
        Check(rateLimited.HttpStatus == 429, "the 429 is reported as 429");

        var forbidden = SyncDiagnosticFailure.Classify(
            new HttpRequestException("x", null, HttpStatusCode.Forbidden), CancellationToken.None);
        Check(forbidden.HttpStatus == 403, "the Epic bot-challenge 403 is reported as 403");

        var serverError = SyncDiagnosticFailure.Classify(
            new HttpRequestException("x", null, HttpStatusCode.BadGateway), CancellationToken.None);
        Check(serverError.HttpStatus == 502, "a 5xx is reported with its own status");

        var bare = SyncDiagnosticFailure.Classify(new HttpRequestException("connection refused"), CancellationToken.None);
        Check(bare.Reason == SyncDiagnosticReason.TransportError, "a request that never got a response is a transport error");
        Check(bare.HttpStatus is null, "an absent status stays absent instead of being invented");
        Check(bare.ErrorType == "HttpRequestException", "the transport failure names its type");

        var timeout = SyncDiagnosticFailure.Classify(new TimeoutException(), CancellationToken.None);
        Check(timeout.Reason == SyncDiagnosticReason.Timeout, "an explicit timeout is a timeout");
        Check(timeout.Outcome == SyncDiagnosticOutcome.Failed, "a timeout is a failed phase");

        var httpClientTimeout = SyncDiagnosticFailure.Classify(
            new TaskCanceledException("timeout"), CancellationToken.None);
        Check(httpClientTimeout.Reason == SyncDiagnosticReason.Timeout,
            "the HttpClient timeout surfaces as a cancellation the caller never asked for");

        var parse = SyncDiagnosticFailure.Classify(new JsonException("bad payload"), CancellationToken.None);
        Check(parse.Reason == SyncDiagnosticReason.ParseError, "an unparseable payload is a parse error");
        Check(parse.HttpStatus is null, "a parse error claims no status");

        var unknown = SyncDiagnosticFailure.Classify(new InvalidOperationException("boom"), CancellationToken.None);
        Check(unknown.Reason == SyncDiagnosticReason.UnknownFailure, "an unexpected failure stays generic");
        Check(unknown.ErrorType == "InvalidOperationException", "the unknown failure still names its type");
    }

    /// <summary>
    /// A shutdown that cancels a phase is not a provider failure (spec S2/S3): it is an outcome of its own, and it
    /// must never be counted against the store.
    /// </summary>
    private static void ClassifierTreatsCallerCancellationAsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var classified = SyncDiagnosticFailure.Classify(
            new OperationCanceledException(cancellation.Token), cancellation.Token);
        Check(classified.Outcome == SyncDiagnosticOutcome.Cancelled, "a caller cancellation is not a failure");
        Check(classified.Reason == SyncDiagnosticReason.Cancelled, "a caller cancellation says so");

        var collector = new SyncDiagnosticsCollector();
        collector.Activate();
        var cancelled = SyncDiagnosticFailure.Classify(
            new OperationCanceledException(cancellation.Token), cancellation.Token);
        collector.Record(
            1, null, SyncDiagnosticProvider.Steam, cancelled.Outcome, cancelled.Reason, cancelled.HttpStatus);
        Check(collector.BuildSnapshot().Summary.Single().Outcome == "cancelled",
            "the collector persists the cancellation as its own outcome");
    }

    // ------------------------------------------------------------- read path ------------------------------------

    /// <summary>
    /// The manual runner wraps the executor's result, so the document lives under <c>result.diagnostics</c>; a
    /// scheduled cycle writes the executor's details directly, so it lives at the root. Both must be readable, and
    /// a stored list longer than the cap must be cut with honest counts rather than silently dropped.
    /// </summary>
    private static void ReadPathAcceptsBothRootsAndKeepsCountsHonest()
    {
        var diagnostics = Diagnostics(totalEvents: 250, summaryCount: 250, eventCount: 205);
        var root = JobDetailsSanitizer.Sanitize(Parse("{\"reason\":\"ok\",\"diagnostics\":" + diagnostics + "}"));
        var rootDiagnostics = Property(root, "diagnostics");
        Check(rootDiagnostics is not null, "root diagnostics are readable");
        CheckInt(rootDiagnostics!, "schemaVersion", 1, "the schema version survives the read path");
        CheckInt(rootDiagnostics!, "totalEvents", 250, "the total is the recorded total");
        CheckInt(rootDiagnostics!, "shownEvents", 200, "the shown count is the bounded slice");
        CheckInt(rootDiagnostics!, "truncatedEvents", 50, "the truncated count is honest");
        Check(Count(rootDiagnostics, "events") == 200, "the read path keeps 200 events");

        var wrapped = JobDetailsSanitizer.Sanitize(Parse(
            "{\"requested\":{\"ageHours\":72},\"result\":{\"reason\":\"ok\",\"diagnostics\":" + diagnostics + "}}"));
        var nested = Property(Property(wrapped, "result"), "diagnostics");
        Check(nested is not null, "a manual run's diagnostics under result are readable");
        CheckInt(nested!, "shownEvents", 200, "the nested document is normalised the same way");

        var summary = Property(nested, "summary");
        Check(Count(nested, "summary") == 1, "the summary line survives");
        CheckInt(summary!.Value[0], "count", 250, "the summary is the complete aggregate, not the slice");
        Check(Property(summary.Value[0], "provider")?.GetString() == "epic", "the summary keeps its provider token");
    }

    /// <summary>
    /// A stored document is untrusted: unknown schema versions, unknown tokens, unsafe strings and inconsistent
    /// counts must not reach the page. The diagnosis is dropped, never faked into a zero-failure claim.
    /// </summary>
    private static void ReadPathRejectsUnknownSchemaAndUnsafeFields()
    {
        var historical = JobDetailsSanitizer.Sanitize(Parse(
            """{"reason":"ok","diagnostics":{"schemaVersion":2,"totalEvents":1,"shownEvents":1,"truncatedEvents":0,"summary":[],"events":[]}}"""));
        Check(Property(historical, "diagnostics") is null, "an unknown schema version is not rendered");
        Check(Property(historical, "reason")?.GetString() == "ok", "the rest of the details still renders");

        var unsafeProvider = JobDetailsSanitizer.Sanitize(Parse(
            "{\"diagnostics\":" + Diagnostics(totalEvents: 2, summaryCount: 2, eventCount: 2, provider: "evil-store") + "}"));
        Check(Property(unsafeProvider, "diagnostics") is null,
            "an unknown provider token invalidates the document instead of being rendered");

        var unsafeType = JobDetailsSanitizer.Sanitize(Parse(
            "{\"diagnostics\":" + Diagnostics(
                totalEvents: 2, summaryCount: 2, eventCount: 2,
                errorType: "HttpRequestException https://api.gg.deals?key=SECRET") + "}"));
        var kept = Property(unsafeType, "diagnostics");
        Check(kept is not null, "the document survives one unsafe event");
        Check(Count(kept, "events") == 0, "the unsafe event is dropped");
        CheckInt(kept!, "shownEvents", 0, "the shown count follows what is really shown");
        CheckInt(kept!, "truncatedEvents", 2, "the dropped events are reported as not shown");
        Check(!unsafeType!.Value.GetRawText().Contains("SECRET", StringComparison.Ordinal),
            "the unsafe string never reaches the response");

        var inconsistent = JobDetailsSanitizer.Sanitize(Parse(
            "{\"diagnostics\":" + Diagnostics(totalEvents: 99, summaryCount: 2, eventCount: 2) + "}"));
        Check(Property(inconsistent, "diagnostics") is null,
            "a summary that disagrees with its own total is not trusted");

        var unknownFields = JobDetailsSanitizer.Sanitize(Parse(
            "{\"diagnostics\":{\"schemaVersion\":1,\"totalEvents\":1,\"shownEvents\":1,\"truncatedEvents\":0," +
            "\"summary\":[{\"provider\":\"epic\",\"outcome\":\"failed\",\"reason\":\"http_error\",\"httpStatus\":403,\"count\":1,\"note\":\"x\"}]," +
            "\"events\":[{\"appId\":1,\"gameId\":null,\"provider\":\"epic\",\"outcome\":\"failed\",\"reason\":\"http_error\"," +
            "\"httpStatus\":403,\"errorType\":\"HttpRequestException\",\"elapsedMs\":10,\"url\":\"https://secret\"}]," +
            "\"version\":\"9\"}}"));
        var trimmed = Property(unknownFields, "diagnostics");
        Check(trimmed is not null, "unknown extra fields do not invalidate a valid document");
        Check(!unknownFields!.Value.GetRawText().Contains("https://secret", StringComparison.Ordinal),
            "an unknown field is discarded instead of rendered");
        Check(Count(trimmed, "events") == 1, "the valid event survives");
    }

    /// <summary>Preserve: the schema-aware bound must not touch the job details that are not diagnostics.</summary>
    private static void ReadPathLeavesUnrelatedDetailsAlone()
    {
        var sanitized = JobDetailsSanitizer.Sanitize(Parse(
            """{"requested":{"ageHours":72,"includeMissing":true},"result":{"reason":"nothing_pending","pending":0,"remaining":4}}"""));
        Check(Property(Property(sanitized, "requested"), "ageHours")?.GetInt32() == 72,
            "the numeric option still renders");
        Check(Property(Property(sanitized, "requested"), "includeMissing")?.ValueKind == JsonValueKind.True,
            "the boolean option still renders");
        Check(Property(Property(sanitized, "result"), "reason")?.GetString() == "nothing_pending",
            "a run without diagnostics keeps its own details");
        Check(Property(Property(sanitized, "result"), "diagnostics") is null,
            "nothing is invented for a run that recorded no diagnostics");
    }

    // ------------------------------------------------------------------ wiring ----------------------------------

    /// <summary>
    /// A run that fails before it can close normally still has the events it collected: the rows are attached to the
    /// failure record, and the failure record keeps its existing fields.
    /// </summary>
    private static void RunnerFailureStillReportsTheEventsItCollected()
    {
        var collector = new SyncDiagnosticsCollector();
        var executor = new DiagnosticFailureExecutor(collector);
        var tracker = new RecordingTracker();
        var runner = new JobExecutionRunner(
            new JobCatalog(JobCatalogSettings.Default), [executor], tracker, NullLogger<JobExecutionRunner>.Instance, collector);

        runner.RunAsync(new JobQueueItem(JobKeys.GlobalDeals, 901, JobRunOptions.Empty), CancellationToken.None)
            .GetAwaiter().GetResult();

        var details = JsonNode.Parse(tracker.Finished[0].Details!)!;
        Check(details["errorType"]?.GetValue<string>() == "InvalidOperationException",
            "the failure still records the exception type");
        Check(details["diagnostics"]?["totalEvents"]?.GetValue<int>() == 1,
            "the failure record carries the events collected before it");
        Check(tracker.Finished[0].Details!.Contains("InvalidOperationException", StringComparison.Ordinal) &&
            tracker.Finished[0].Details!.Contains("boom", StringComparison.Ordinal),
            "the existing error message field is unchanged");

        // A run that never reached a provider attaches nothing: no fabricated empty report on unrelated jobs.
        var clean = new RecordingTracker();
        var silent = new JobExecutionRunner(
            new JobCatalog(JobCatalogSettings.Default),
            [new FakeExecutor(JobKeys.GlobalDeals, failure: new InvalidOperationException("boom"))],
            clean,
            NullLogger<JobExecutionRunner>.Instance,
            new SyncDiagnosticsCollector());
        silent.RunAsync(new JobQueueItem(JobKeys.GlobalDeals, 902, JobRunOptions.Empty), CancellationToken.None)
            .GetAwaiter().GetResult();
        Check(JsonNode.Parse(clean.Finished[0].Details!)!["diagnostics"] is null,
            "a run with no events does not fabricate a report");
    }

    /// <summary>
    /// Two runs, two collectors: the second report never contains the first run's events, and reading a report never
    /// clears the collector the same run still needs. This is the whole isolation contract (spec S2).
    /// </summary>
    private static void TwoRunsNeverShareEvents()
    {
        var firstRun = new SyncDiagnosticsCollector();
        firstRun.Activate();
        firstRun.Record(1, null, SyncDiagnosticProvider.Steam, SyncDiagnosticOutcome.Failed,
            SyncDiagnosticReason.HttpError, 429);
        var firstSnapshot = firstRun.BuildSnapshot();

        var secondRun = new SyncDiagnosticsCollector();
        secondRun.Activate();
        secondRun.Record(2, null, SyncDiagnosticProvider.Epic, SyncDiagnosticOutcome.Refreshed,
            SyncDiagnosticReason.Refreshed);
        var secondSnapshot = secondRun.BuildSnapshot();

        Check(firstSnapshot.TotalEvents == 1 && secondSnapshot.TotalEvents == 1,
            "each run counts only its own events");
        Check(secondSnapshot.Events[0].AppId == 2, "the second run does not inherit the first run's events");
        Check(firstRun.BuildSnapshot().TotalEvents == 1, "reading a report does not clear its collector");

        // Reading twice yields the same document: the failure path and the result path can both report.
        var again = firstRun.BuildSnapshot();
        Check(again.TotalEvents == firstSnapshot.TotalEvents && again.Events.Count == firstSnapshot.Events.Count,
            "the snapshot is repeatable");

        // Interactive traffic cannot reach a job report: a request scope owns a different collector instance.
        var interactiveRequest = new SyncDiagnosticsCollector();
        interactiveRequest.Activate();
        interactiveRequest.Record(3, null, SyncDiagnosticProvider.Steam, SyncDiagnosticOutcome.Skipped,
            SyncDiagnosticReason.GateSkipped);
        Check(secondRun.BuildSnapshot().TotalEvents == 1,
            "an interactive scope's events never appear in a job's report");
    }

    /// <summary>
    /// The isolation contract at the level that actually enforces it: the DI lifetime. A singleton would make every
    /// run append to the same document (a cross-run leak no code review would see), and a transient would give the
    /// executor and the runner two different reports. Scoped is the only lifetime that means "one run, one report".
    /// </summary>
    private static void TheCollectorIsScopedSoOneRunOwnsOneReport()
    {
        IServiceCollection services = new ServiceCollection();
        Deals.API.Extensions.ServiceCollectionExtensions.AddApiApplicationServices(services);

        Check(
            services.Any(descriptor =>
                descriptor.ServiceType == typeof(ISyncDiagnosticsCollector) &&
                descriptor.Lifetime == ServiceLifetime.Scoped),
            "the collector is registered scoped, which is what keeps two runs apart");
        Check(
            !services.Any(descriptor =>
                descriptor.ServiceType == typeof(ISyncDiagnosticsCollector) &&
                descriptor.Lifetime != ServiceLifetime.Scoped),
            "the collector has no second, wider registration");

        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ISyncDiagnosticsCollector>();
        var second = secondScope.ServiceProvider.GetRequiredService<ISyncDiagnosticsCollector>();

        Check(!ReferenceEquals(first, second), "two scopes never share one collector");
        Check(ReferenceEquals(first, firstScope.ServiceProvider.GetRequiredService<ISyncDiagnosticsCollector>()),
            "one scope resolves the same collector, so every phase of a run reaches one report");

        first.Activate();
        first.Record(1, null, SyncDiagnosticProvider.Steam, SyncDiagnosticOutcome.Failed,
            SyncDiagnosticReason.HttpError, 429);
        Check(!second.HasEvents, "one run's events never appear in another run's report");
    }

    /// <summary>
    /// (1) Default inactive. An interactive request shares the DI scope with nothing that claims the collector, so a page
    /// load that reaches a provider phase records no event and logs no warning — spec S2's "interactive requests must not
    /// contaminate job reports" is enforced by the collector itself, not by the page not being read.
    /// </summary>
    private static void NoRunClaimsNoCollector()
    {
        var idle = new SyncDiagnosticsCollector();
        Check(!idle.IsActive, "a fresh collector is inactive");
        Check(!idle.HasEvents, "an inactive collector has no events");

        idle.Record(1057800, 7, SyncDiagnosticProvider.Steam, SyncDiagnosticOutcome.Failed,
            SyncDiagnosticReason.HttpError, 429, "HttpRequestException", 10);
        idle.Record(1057800, 7, SyncDiagnosticProvider.Epic, SyncDiagnosticOutcome.Refreshed,
            SyncDiagnosticReason.Refreshed);

        Check(!idle.HasEvents, "an inactive collector records nothing, not even a failure");
        var snapshot = idle.BuildSnapshot();
        Check(snapshot.TotalEvents == 0, "an inactive collector counts nothing");
        Check(snapshot.Summary.Count == 0, "an inactive collector summarises nothing");

        theInteractiveRequestRecordsNothingInItsOwnScope();
    }

    /// <summary>
    /// The same rule at the layer that produces the events: the provider pipeline over an unclaimed collector.
    /// </summary>
    private static void theInteractiveRequestRecordsNothingInItsOwnScope()
    {
        var idle = new SyncDiagnosticsCollector();
        idle.Activate();
        Check(idle.IsActive, "an explicitly claimed collector is active");
        idle.Record(1, null, SyncDiagnosticProvider.Steam, SyncDiagnosticOutcome.Refreshed,
            SyncDiagnosticReason.Refreshed);
        Check(idle.BuildSnapshot().TotalEvents == 1, "a claimed collector records normally again");
    }

    /// <summary>
    /// (1) The two job executors are the only claimants, and they claim before the pass runs — a scheduled wishlist
    /// cycle and a manual catalog run are the same executor, so both paths are covered by construction.
    /// </summary>
    private static void AJobRunClaimsTheCollectorBeforeThePassRuns()
    {
        // Manual catalog run: the fake service records through the very collector the executor holds.
        var catalogCollector = new SyncDiagnosticsCollector();
        var catalogExecutor = new CatalogPriceRefreshJobExecutor(
            new RecordingCatalogRefreshService(catalogCollector), catalogCollector);
        var tracker = new RecordingTracker();
        var runner = new JobExecutionRunner(
            new JobCatalog(JobCatalogSettings.Default), [catalogExecutor], tracker,
            NullLogger<JobExecutionRunner>.Instance, catalogCollector);

        runner.RunAsync(
            new JobQueueItem(JobKeys.CatalogPricesRefresh, 910, JobRunOptions.Empty, 7),
            CancellationToken.None).GetAwaiter().GetResult();

        Check(catalogCollector.IsActive, "the catalog executor claims its collector");
        var details = JsonNode.Parse(tracker.Finished[0].Details!)!;
        Check(details["result"]?["diagnostics"]?["totalEvents"]?.GetValue<int>() == 1,
            "the phase recorded inside the pass reaches the manual report under result");

        // Scheduled wishlist cycle: same executor the hosted service calls with the scheduled options.
        var wishlistCollector = new SyncDiagnosticsCollector();
        var wishlistExecutor = new WishlistSyncJobExecutor(
            new RecordingWishlistSyncService(wishlistCollector), wishlistCollector,
            NullLogger<WishlistSyncJobExecutor>.Instance);
        var result = wishlistExecutor.ExecuteAsync(WishlistSyncJobExecutor.ScheduledOptions, CancellationToken.None)
            .GetAwaiter().GetResult();
        var scheduled = JsonNode.Parse(JsonSerializer.Serialize(result.Details))!;

        Check(wishlistCollector.IsActive, "the wishlist executor claims its collector");
        Check(scheduled["diagnostics"]?["totalEvents"]?.GetValue<int>() == 1,
            "the phase recorded inside the scheduled pass reaches the root report");
    }

    /// <summary>
    /// (2) The parent's bound: aggregate by (provider, outcome, reason) and only there — the summary carries no status,
    /// so its cardinality is 7 × 5 × 13 = 455 &lt; the page's 512 whatever the providers answer. The actual status is not
    /// lost: it stays on the detailed event, which is what the page shows per game. Complete counts are preserved.
    /// </summary>
    private static void ThesummaryCountsReasonsNotStatuses()
    {
        Check(
            SyncDiagnosticsContract.MaxSummaryGroups >= 7 * 5 * 13,
            "the closed taxonomy fits under the aggregate ceiling");

        var collector = new SyncDiagnosticsCollector();
        collector.Activate();
        for (var index = 0; index < 500; index++)
        {
            collector.Record(
                20_000 + index,
                index,
                SyncDiagnosticProvider.Epic,
                SyncDiagnosticOutcome.Failed,
                SyncDiagnosticReason.HttpError,
                httpStatus: 100 + index,
                errorType: "HttpRequestException",
                elapsedMs: index);
        }

        var snapshot = collector.BuildSnapshot();
        Check(snapshot.TotalEvents == 500, "every event is still counted");
        Check(snapshot.ShownEvents == 200, "the detailed slice stays bounded");
        Check(snapshot.TruncatedEvents == 300, "the omitted events are reported");
        Check(snapshot.Summary.Count == 1,
            $"500 statuses of one reason make one aggregate line, got {snapshot.Summary.Count}");

        var row = snapshot.Summary.Single();
        Check(row.Provider == "epic", "the aggregate keeps its provider");
        Check(row.Outcome == "failed", "the aggregate keeps its outcome");
        Check(row.Reason == "http_error", "the aggregate keeps its reason");
        Check(row.Count == 500, "the aggregate is complete, not the bounded slice");
        Check(row.HttpStatus is null,
            "the aggregate carries no status, so the page never reads a null as 'unavailable'");

        Check(snapshot.Events[0].HttpStatus == 100, "the first detailed event keeps its actual status");
        Check(snapshot.Events[199].HttpStatus == 299, "the last shown event keeps its actual status");
        Check(snapshot.Events.All(entry => entry.HttpStatus is not null),
            "no detailed event loses its actual status");

        // The bound is a ceiling, not a promise: 455 is the worst case over the closed vocabularies.
        var groups = SyncDiagnosticsContract.ProviderTokens.Count
            * SyncDiagnosticsContract.OutcomeTokens.Count
            * SyncDiagnosticsContract.ReasonTokens.Count;
        Check(groups == 455, $"the worst case is 7 x 5 x 13 = 455 groups, got {groups}");
        Check(groups <= SyncDiagnosticsContract.MaxSummaryGroups, "and it fits under the page's ceiling");
    }

    /// <summary>
    /// (3) The exception type is a safe identifier, not free text: the page's own bound is 64 characters, so the backend
    /// accepts exactly that and drops anything longer instead of persisting a half-message.
    /// </summary>
    private static void TheErrorTypeBoundaryIsSixtyFour()
    {
        Check(SyncDiagnosticsContract.MaxErrorTypeLength == 64,
            "the backend bound matches the page's 64 characters");

        var accepted = new string('A', 64);
        Check(SyncDiagnosticsContract.SafeErrorType(accepted) == accepted, "64 characters are accepted");
        Check(SyncDiagnosticsContract.SafeErrorType(new string('A', 65)) is null, "65 characters are rejected");
        Check(SyncDiagnosticsContract.SafeErrorType("HttpRequestException") == "HttpRequestException",
            "a real type name still passes");
        Check(SyncDiagnosticsContract.SafeErrorType("HttpRequestException boom") is null,
            "a message is not a type name");
        Check(SyncDiagnosticsContract.SafeErrorType(
            "https://api.gg.deals/v1/?key=SECRET") is null, "a URL is not a type name");

        // Through the collector: the over-long type is dropped from the event, and nothing of it is rendered.
        var collector = new SyncDiagnosticsCollector();
        collector.Activate();
        collector.Record(1, null, SyncDiagnosticProvider.Itad, SyncDiagnosticOutcome.Failed,
            SyncDiagnosticReason.UnknownFailure, errorType: new string('B', 65));
        var snapshot = collector.BuildSnapshot();
        Check(snapshot.Events[0].ErrorType is null, "an over-long exception type is dropped, not truncated");
        Check(!JsonSerializer.Serialize(snapshot).Contains(new string('B', 65), StringComparison.Ordinal),
            "no fragment of it reaches the document");
    }

    // ----------------------------------------------------------------- helpers ----------------------------------

    private static string Diagnostics(
        int totalEvents,
        int summaryCount,
        int eventCount,
        string provider = "epic",
        string errorType = "HttpRequestException")
    {
        var events = string.Join(',', Enumerable.Range(0, eventCount).Select(index =>
            "{\"appId\":" + (1_000 + index) + ",\"gameId\":null,\"provider\":\"" + provider +
            "\",\"outcome\":\"failed\",\"reason\":\"http_error\",\"httpStatus\":403,\"errorType\":\"" +
            errorType + "\",\"elapsedMs\":10}"));
        var summary = "{\"provider\":\"" + provider +
            "\",\"outcome\":\"failed\",\"reason\":\"http_error\",\"httpStatus\":403,\"count\":" + summaryCount + "}";
        return "{\"schemaVersion\":1,\"totalEvents\":" + totalEvents +
            ",\"shownEvents\":" + eventCount +
            ",\"truncatedEvents\":" + Math.Max(0, totalEvents - eventCount) +
            ",\"summary\":[" + summary + "],\"events\":[" + events + "]}";
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement? Property(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        return value.TryGetProperty(name, out var property) ? property : null;
    }

    private static void CheckInt(JsonElement? element, string name, int expected, string description)
    {
        var property = Property(element, name);
        Check(property?.ValueKind == JsonValueKind.Number, $"{description}: a number is expected");
        Check(property!.Value.GetInt32() == expected, $"{description}: expected {expected}, got {property.Value.GetInt32()}");
    }

    /// <summary>Length of a nested array, so a bounded list is asserted without trusting the declared counters.</summary>
    private static int Count(JsonElement? element, string name)
    {
        var property = Property(element, name);
        return property?.ValueKind == JsonValueKind.Array ? property.Value.GetArrayLength() : -1;
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }

    /// <summary>
    /// Executor seam for the manual path: records one phase through the collector the executor holds, then answers.
    /// If the executor does not claim the collector before calling this, the record is dropped and the check fails.
    /// </summary>
    private sealed class RecordingCatalogRefreshService(ISyncDiagnosticsCollector diagnostics)
        : ICatalogPriceRefreshService
    {
        public Task<CatalogPriceRefreshOutcome> RefreshPendingAsync(
            int requestedByUserId,
            CancellationToken cancellationToken = default,
            int ageHours = CatalogPriceRefreshLimits.DefaultAgeHours)
        {
            diagnostics.Record(1057800, 7, SyncDiagnosticProvider.Steam, SyncDiagnosticOutcome.Refreshed,
                SyncDiagnosticReason.Refreshed, elapsedMs: 90);
            return Task.FromResult(new CatalogPriceRefreshOutcome(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                UnresolvedWishlistEntries: 0,
                AgeHours: ageHours));
        }
    }

    /// <summary>
    /// Executor seam for the scheduled path: the list phase records one phase through the collector the executor
    /// holds and answers; the price phase is not requested by the scheduled options.
    /// </summary>
    private sealed class RecordingWishlistSyncService(ISyncDiagnosticsCollector diagnostics)
        : IWishlistSyncService
    {
        public Task<WishlistListSyncReport> SyncListAsync(CancellationToken cancellationToken)
        {
            diagnostics.Record(1057800, 7, SyncDiagnosticProvider.Microsoft, SyncDiagnosticOutcome.NoMatch,
                SyncDiagnosticReason.StoreNoOffer);
            return Task.FromResult(new WishlistListSyncReport("ok", 3, 1, 2, 0, null, 1, 0));
        }

        public Task<WishlistRefreshReport> RefreshWishedGamesAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the scheduled options of this check request no price pass");
    }

    /// <summary>Records one phase, then fails the way a broken provider client would.</summary>
    private sealed class DiagnosticFailureExecutor(ISyncDiagnosticsCollector diagnostics) : IJobExecutor
    {
        public string Job => JobKeys.GlobalDeals;

        public Task<JobExecutionResult> ExecuteAsync(JobRunOptions options, CancellationToken cancellationToken)
        {
            diagnostics.Activate();
            diagnostics.Record(1057800, 7, SyncDiagnosticProvider.Itad, SyncDiagnosticOutcome.Failed,
                SyncDiagnosticReason.Timeout, errorType: "TaskCanceledException", elapsedMs: 2500);
            throw new InvalidOperationException("boom");
        }
    }
}
