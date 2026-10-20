#nullable enable
using System.Text.Json;
using Deals.API.Models.Jobs;
using Deals.API.Services.Jobs;
using Deals.Models.Entities;
using JobsChecks;
using Microsoft.Extensions.Logging.Abstractions;

// Deterministic checks for the admin jobs catalog and manual dispatch (spec S2). Run with:
//   dotnet run --project checks/Jobs.Checks
// Nothing here touches a database, a provider or a real job: the queue is the production in-memory one,
// and the run log, the executors and the clock are fakes. A thrown Exception or a non-zero exit marks the
// suite failed.

JobCatalogChecks.Run();
JobOptionChecks.Run();
JobQueueChecks.Run();
await JobSubmissionChecks.RunAsync();
await JobExecutionChecks.RunAsync();
JobSanitizerChecks.Run();
JobCompatibilityChecks.Run();

Console.WriteLine("PASS: all jobs catalog/dispatch checks");

internal static class JobCatalogChecks
{
    public static void Run()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        Check(catalog.All.Count == 7, "catalog exposes exactly the seven registered hosted jobs");
        Check(
            catalog.All.Select(entry => entry.Job).ToHashSet(StringComparer.Ordinal)
                .SetEquals(JobKeys.All),
            "catalog keys are the stable job keys");

        foreach (var key in JobKeys.All)
        {
            Check(catalog.Find(key) is not null, $"catalog finds {key}");
        }

        Check(catalog.Find("bundles.unknown-sync") is null, "unknown job is not found");
        Check(catalog.Find(null) is null, "missing job is not found");
        Check(catalog.Find("  ") is null, "blank job is not found");
        Check(catalog.Find("WISHLIST.SYNC") is null, "job lookup is ordinal, not case-insensitive");

        // Every job that runs in the host owns a session advisory lock, manual runs included. Two jobs
        // sharing a key would silently skip each other, so the keys must be pairwise distinct.
        var lockKeys = catalog.All.Select(entry => entry.AdvisoryLockKey).ToList();
        Check(lockKeys.All(key => key is not null), "every job declares an advisory lock key");
        Check(
            lockKeys.Distinct().Count() == lockKeys.Count,
            "advisory lock keys are pairwise distinct");
        Check(
            catalog.Find(JobKeys.GlobalBundles)!.AdvisoryLockKey == 7_831_442_093L,
            "global bundle sweep keeps its production lock key");
        Check(
            catalog.Find(JobKeys.WishlistSync)!.AdvisoryLockKey == 7_831_442_091L,
            "wishlist sync keeps its production lock key");

        foreach (var entry in catalog.All)
        {
            Check(!string.IsNullOrWhiteSpace(entry.DisplayName), $"{entry.Job} has a display name");
            Check(!string.IsNullOrWhiteSpace(entry.Description), $"{entry.Job} has a description");
            Check(!string.IsNullOrWhiteSpace(entry.Schedule), $"{entry.Job} declares its schedule");
        }

        // Disabled providers must be reported as unavailable with a reason, never hidden or shown as if
        // they could run. An enabled job with a disabled reason would be a lie in the opposite direction.
        var disabled = new JobCatalog(JobCatalogSettings.Default with
        {
            GgDealsBundlesEnabled = false,
            GlobalBundlesEnabled = false,
            NtfyEnabled = false
        });

        foreach (var key in new[] { JobKeys.GgDealsBundles, JobKeys.GlobalBundles, JobKeys.NtfyAlerts })
        {
            var entry = disabled.Find(key)!;
            Check(!entry.Enabled, $"{key} reports disabled");
            Check(!string.IsNullOrWhiteSpace(entry.DisabledReason), $"{key} explains why it is disabled");
        }

        foreach (var entry in disabled.All.Where(entry => entry.Enabled))
        {
            Check(entry.DisabledReason is null, $"enabled job {entry.Job} carries no disabled reason");
        }

        // Only the purge destroys data, and only it demands a second confirmation.
        var destructive = catalog.All.Where(entry => entry.Destructive).Select(entry => entry.Job).ToList();
        Check(destructive.SequenceEqual(new[] { JobKeys.Purge }), "only the purge is destructive");
        Check(catalog.Find(JobKeys.Purge)!.RequiresConfirmation, "the purge requires confirmation");
        Check(catalog.All.Where(entry => !entry.Destructive).All(entry => !entry.RequiresConfirmation),
            "no non-destructive job requires confirmation");

        // Options exist only where the job has a bounded choice. The three sweeps have none: overriding
        // their page caps would be an arbitrary configuration override, not an option.
        Check(catalog.Find(JobKeys.WishlistSync)!.Options.Count == 1,
            "wishlist exposes exactly one bounded option");
        Check(catalog.Find(JobKeys.NtfyAlerts)!.Options.Count == 2, "ntfy exposes its two phase switches");
        foreach (var key in new[] { JobKeys.GlobalBundles, JobKeys.GgDealsBundles, JobKeys.GlobalDeals })
        {
            Check(catalog.Find(key)!.Options.Count == 0, $"{key} has no options to invent");
        }

        Check(catalog.Find(JobKeys.FxRateRefresh)!.Options.Count == 0, "fx refresh has no options");
        Check(catalog.Find(JobKeys.Purge)!.Options.Count == 1, "purge exposes only the dry run switch");
        Console.WriteLine("PASS: jobs catalog checks");
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }
}

internal static class JobOptionChecks
{
    public static void Run()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        // Defaults: booleans resolve to the job's documented default, never to an invented value.
        var wishlistDefaults = Parse(catalog, JobKeys.WishlistSync, null);
        Check(wishlistDefaults.Succeeded, "absent options fall back to defaults");
        Check(!wishlistDefaults.Options.IncludePriceRefresh, "wishlist price refresh is off by default");

        var ntfyDefaults = Parse(catalog, JobKeys.NtfyAlerts, null);
        Check(ntfyDefaults.Options.ProduceAlerts, "ntfy produces by default");
        Check(ntfyDefaults.Options.DispatchAlerts, "ntfy dispatches by default");

        var wishlist = Parse(catalog, JobKeys.WishlistSync, """{"includePriceRefresh": true}""");
        Check(wishlist.Succeeded && wishlist.Options.IncludePriceRefresh, "explicit wishlist option applies");

        var ntfy = Parse(catalog, JobKeys.NtfyAlerts, """{"dispatch": false}""");
        Check(ntfy.Succeeded && ntfy.Options.ProduceAlerts && !ntfy.Options.DispatchAlerts,
            "one ntfy switch does not reset the other");

        // Unknown keys are rejected instead of being ignored: a typo must not run the job with the
        // default behaviour the operator thought they had overridden.
        var unknown = Parse(catalog, JobKeys.WishlistSync, """{"includePrices": true}""");
        Check(!unknown.Succeeded, "unknown option is rejected");
        Check(unknown.Error?.Contains("includePrices", StringComparison.Ordinal) == true,
            "unknown option error names the option");

        var wrongType = Parse(catalog, JobKeys.WishlistSync, """{"includePriceRefresh": "yes"}""");
        Check(!wrongType.Succeeded, "non-boolean option is rejected");
        Check(wrongType.Error?.Contains("true o false", StringComparison.Ordinal) == true,
            "non-boolean option error explains the expected type");

        var notAnObject = Parse(catalog, JobKeys.WishlistSync, """[true]""");
        Check(!notAnObject.Succeeded, "options must be a JSON object");
        Check(notAnObject.Error is not null, "non-object options carry a message");

        var notForThisJob = Parse(catalog, JobKeys.GlobalBundles, """{"includePriceRefresh": true}""");
        Check(!notForThisJob.Succeeded, "an option of another job is rejected");

        // The destructive purge needs an explicit confirmation, and a dry run is the one safe way to
        // execute it without one.
        var purgeUnconfirmed = Parse(catalog, JobKeys.Purge, null, confirm: false);
        Check(!purgeUnconfirmed.Succeeded, "purge without confirmation is rejected");
        Check(purgeUnconfirmed.RequiresConfirmation, "purge rejection is a confirmation requirement");

        var purgeConfirmed = Parse(catalog, JobKeys.Purge, null, confirm: true);
        Check(purgeConfirmed.Succeeded && !purgeConfirmed.Options.DryRun, "confirmed purge parses");

        var purgeDryRun = Parse(catalog, JobKeys.Purge, """{"dryRun": true}""", confirm: false);
        Check(purgeDryRun.Succeeded && purgeDryRun.Options.DryRun, "dry run needs no confirmation");

        var bundleConfirm = Parse(catalog, JobKeys.GlobalBundles, null, confirm: false);
        Check(bundleConfirm.Succeeded, "non-destructive jobs ignore the confirmation flag");
        Console.WriteLine("PASS: jobs option checks");
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }

    private static JobOptionParseResult Parse(
        JobCatalog catalog,
        string job,
        string? options,
        bool confirm = true)
    {
        using var document = options is null ? null : JsonDocument.Parse(options);
        return JobOptionParser.Parse(catalog.Find(job)!, document?.RootElement, confirm);
    }
}

internal static class JobQueueChecks
{
    public static void Run()
    {
        var queue = new JobRunQueue();
        var item = new JobQueueItem(JobKeys.FxRateRefresh, 7, JobRunOptions.Empty);

        Check(!queue.TryPublish(item), "publishing without a reservation is refused");

        Check(queue.TryReserve(JobKeys.FxRateRefresh), "first reservation wins");
        Check(!queue.TryReserve(JobKeys.FxRateRefresh), "second reservation for the same job is refused");
        Check(queue.TryReserve(JobKeys.GlobalBundles), "a different job reserves independently");

        Check(queue.TryPublish(item), "a reserved job publishes");
        Check(!queue.TryReserve(JobKeys.FxRateRefresh), "a published job stays busy until completed");

        queue.Release(JobKeys.GlobalBundles);
        Check(queue.TryReserve(JobKeys.GlobalBundles), "releasing one job frees only that job");

        var drained = queue.Drain();
        Check(drained.Count == 1 && drained[0].JobRunId == 7, "drain returns the pending item");
        Check(queue.TryReserve(JobKeys.FxRateRefresh), "drained jobs are no longer reserved");

        queue.Release(JobKeys.FxRateRefresh);
        Check(queue.TryReserve(JobKeys.FxRateRefresh), "the job can be reserved again after release");
        queue.Release(JobKeys.FxRateRefresh);
        queue.Release(JobKeys.GlobalBundles);
        Console.WriteLine("PASS: jobs queue checks");
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }
}

internal static class JobSubmissionChecks
{
    public static async Task RunAsync()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        await NotFoundAsync(catalog);
        await DisabledAsync(catalog);
        await InvalidAsync(catalog);
        await ConfirmationRequiredAsync(catalog);
        await SubmittedAsync(catalog);
        await DuplicateAsync(catalog);
        await RunningElsewhereAsync(catalog);
        await QueueFailureAsync(catalog);

        // Half of a submission is not a submission: a failed log write must free the reservation so the
        // operator can retry instead of being locked out by a job that never started.
        var queue = new FakeJobRunQueue();
        var tracker = new RecordingTracker { Throwing = true };
        var failing = new JobSubmissionService(catalog, queue, tracker, NullLogger<JobSubmissionService>.Instance);
        var threw = false;
        try { await failing.SubmitAsync(JobKeys.FxRateRefresh, null, false, CancellationToken.None); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "a log write failure surfaces instead of being swallowed");
        Check(queue.TryReserve(JobKeys.FxRateRefresh), "a failed submission frees its reservation");
        Console.WriteLine("PASS: jobs submission checks");
    }

    private static async Task NotFoundAsync(JobCatalog catalog)
    {
        var service = Service(catalog, out _, out var tracker);
        var outcome = await service.SubmitAsync("nope", null, false, CancellationToken.None);
        Check(outcome.Status == JobSubmitStatus.NotFound, "unknown job is not found");
        Check(tracker.Started.Count == 0, "unknown job never starts a run");
    }

    private static async Task DisabledAsync(JobCatalog catalog)
    {
        var disabledCatalog = new JobCatalog(JobCatalogSettings.Default with { NtfyEnabled = false });
        var service = Service(disabledCatalog, out var queue, out var tracker);
        var outcome = await service.SubmitAsync(JobKeys.NtfyAlerts, null, false, CancellationToken.None);
        Check(outcome.Status == JobSubmitStatus.Disabled, "disabled job is rejected");
        Check(outcome.Message?.Contains("deshabilitado", StringComparison.OrdinalIgnoreCase) == true,
            "disabled rejection says so in Spanish");
        Check(tracker.Started.Count == 0, "disabled job never starts a run");
        Check(queue.ReserveCalls == 0, "disabled job never reserves the queue");
        // The same job is launchable as soon as its switch is on: availability is configuration, not a
        // property of the job itself.
        var enabledCatalog = new JobCatalog(JobCatalogSettings.Default with { NtfyEnabled = true });
        Check(enabledCatalog.Find(JobKeys.NtfyAlerts)!.Enabled, "the same job is available when its switch is on");
    }

    private static async Task InvalidAsync(JobCatalog catalog)
    {
        var service = Service(catalog, out var queue, out var tracker);
        using var options = JsonDocument.Parse("""{"includePriceRefresh":"maybe"}""");
        var outcome = await service.SubmitAsync(JobKeys.WishlistSync, options.RootElement, false, CancellationToken.None);
        Check(outcome.Status == JobSubmitStatus.Invalid, "invalid options are rejected");
        Check(outcome.Message is not null, "invalid options carry the validation message");
        Check(tracker.Started.Count == 0, "invalid options never start a run");
        Check(queue.ReserveCalls == 0, "invalid options never reserve the queue");
    }

    private static async Task ConfirmationRequiredAsync(JobCatalog catalog)
    {
        var service = Service(catalog, out _, out var tracker);
        var outcome = await service.SubmitAsync(JobKeys.Purge, null, false, CancellationToken.None);
        Check(outcome.Status == JobSubmitStatus.ConfirmationRequired, "unconfirmed purge is rejected");
        Check(tracker.Started.Count == 0, "unconfirmed purge never starts a run");
    }

    private static async Task SubmittedAsync(JobCatalog catalog)
    {
        var service = Service(catalog, out var queue, out var tracker);
        var outcome = await service.SubmitAsync(JobKeys.WishlistSync, null, false, CancellationToken.None);

        Check(outcome.Status == JobSubmitStatus.Submitted, "valid request is submitted");
        Check(outcome.JobRunId == 100, "the response carries the created run id");
        Check(outcome.RequestedAt is not null, "the response carries the request timestamp");
        Check(tracker.Started.SequenceEqual(new[] { JobKeys.WishlistSync }),
            "the run is recorded as a manual run of that job");
        Check(queue.Published.Count == 1, "the request is queued exactly once");
        Check(queue.Published[0].Job == JobKeys.WishlistSync, "the queued item names the job");
        Check(queue.Published[0].JobRunId == 100, "the queued item carries the run id");
    }

    private static async Task DuplicateAsync(JobCatalog catalog)
    {
        var service = Service(catalog, out var queue, out var tracker);
        await service.SubmitAsync(JobKeys.FxRateRefresh, null, false, CancellationToken.None);
        var second = await service.SubmitAsync(JobKeys.FxRateRefresh, null, false, CancellationToken.None);

        Check(second.Status == JobSubmitStatus.Duplicate, "a queued job cannot be queued twice");
        Check(tracker.Started.Count == 1, "the duplicate never starts a second run");
        Check(queue.Published.Count == 1, "the duplicate never reaches the dispatcher");
        Check(second.Message?.Contains("ejecución", StringComparison.OrdinalIgnoreCase) == true,
            "duplicate rejection explains the conflict");
    }

    private static async Task RunningElsewhereAsync(JobCatalog catalog)
    {
        var service = Service(catalog, out var queue, out var tracker);
        tracker.Busy = true;
        var outcome = await service.SubmitAsync(JobKeys.FxRateRefresh, null, false, CancellationToken.None);
        Check(outcome.Status == JobSubmitStatus.Duplicate, "a run in progress blocks a second request");
        Check(tracker.Started.Count == 0, "the blocked request never starts a run");
        Check(queue.ReserveCalls == 0, "the blocked request never reserves the queue");
    }

    private static async Task QueueFailureAsync(JobCatalog catalog)
    {
        var service = Service(catalog, out var queue, out var tracker);
        queue.FailPublish = true;
        var outcome = await service.SubmitAsync(JobKeys.FxRateRefresh, null, false, CancellationToken.None);

        Check(outcome.Status == JobSubmitStatus.Duplicate, "a refused publish is reported as a conflict");
        Check(queue.TryReserve(JobKeys.FxRateRefresh), "a refused publish frees the reservation");
        queue.Release(JobKeys.FxRateRefresh);
        // The row created before publishing must not stay 'running' forever: it is closed as failed with
        // the reason, so the history tells the truth and does not block future requests.
        Check(tracker.Finished.Count == 1, "the orphaned run row is closed");
        Check(tracker.Finished[0].Status == JobRunStatuses.Failed, "the orphaned run row is failed");
        Check(tracker.Finished[0].Details?.Contains("queue", StringComparison.OrdinalIgnoreCase) == true,
            "the orphaned run row records the queue reason");
        Check(tracker.Started.Count == 1, "the orphaned row was created before publishing");
    }

    private static JobSubmissionService Service(JobCatalog catalog, out FakeJobRunQueue queue, out RecordingTracker tracker)
    {
        queue = new FakeJobRunQueue();
        tracker = new RecordingTracker();
        return new JobSubmissionService(catalog, queue, tracker, NullLogger<JobSubmissionService>.Instance);
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }
}

internal static class JobExecutionChecks
{
    public static async Task RunAsync()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        await OkAsync(catalog);
        await DegradedAsync(catalog);
        await ThrowingAsync(catalog);
        await UnknownJobAsync(catalog);
        Console.WriteLine("PASS: jobs execution checks");
    }

    private static async Task OkAsync(JobCatalog catalog)
    {
        var tracker = new RecordingTracker();
        var executor = new FakeExecutor(JobKeys.WishlistSync, new JobExecutionResult(true, new { refreshed = 3 }));
        var runner = Runner(catalog, tracker, executor);
        var options = new JobRunOptions(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [JobOptionKeys.IncludePriceRefresh] = true
        });

        await runner.RunAsync(new JobQueueItem(JobKeys.WishlistSync, 501, options), CancellationToken.None);

        Check(executor.Calls == 1, "the executor runs once");
        Check(executor.LastOptions?.IncludePriceRefresh == true, "the submitted options reach the executor");
        Check(tracker.Finished.Count == 1 && tracker.Finished[0].JobRunId == 501, "the same run row is closed");
        Check(tracker.Finished[0].Status == JobRunStatuses.Ok, "a successful execution is recorded as ok");
        Check(tracker.Finished[0].Details?.Contains("refreshed", StringComparison.Ordinal) == true,
            "the executor details are recorded");
    }

    private static async Task DegradedAsync(JobCatalog catalog)
    {
        var tracker = new RecordingTracker();
        var executor = new FakeExecutor(
            JobKeys.GlobalBundles,
            new JobExecutionResult(false, new { completed = false, reason = "ProviderError" }));
        var runner = Runner(catalog, tracker, executor);

        await runner.RunAsync(new JobQueueItem(JobKeys.GlobalBundles, 502, JobRunOptions.Empty), CancellationToken.None);

        Check(tracker.Finished.Count == 1, "a degraded execution still closes its run");
        Check(tracker.Finished[0].Status == JobRunStatuses.Failed, "a degraded execution is recorded as failed");
        Check(tracker.Finished[0].Details?.Contains("ProviderError", StringComparison.Ordinal) == true,
            "the degraded reason is recorded");
    }

    private static async Task ThrowingAsync(JobCatalog catalog)
    {
        var tracker = new RecordingTracker();
        var executor = new FakeExecutor(JobKeys.GlobalDeals, failure: new InvalidOperationException("boom"));
        var runner = Runner(catalog, tracker, executor);

        await runner.RunAsync(new JobQueueItem(JobKeys.GlobalDeals, 503, JobRunOptions.Empty), CancellationToken.None);

        Check(tracker.Finished.Count == 1, "an exception still closes its run");
        Check(tracker.Finished[0].Status == JobRunStatuses.Failed, "an exception is recorded as failed");
        Check(tracker.Finished[0].Details?.Contains("InvalidOperationException", StringComparison.Ordinal) == true,
            "the failure records the exception type");
    }

    private static async Task UnknownJobAsync(JobCatalog catalog)
    {
        var tracker = new RecordingTracker();
        var executor = new FakeExecutor(JobKeys.FxRateRefresh);
        var runner = Runner(catalog, tracker, executor);

        await runner.RunAsync(new JobQueueItem("gone.sync", 504, JobRunOptions.Empty), CancellationToken.None);

        Check(executor.Calls == 0, "an unknown job never reaches an executor");
        Check(tracker.Finished.Count == 1, "an unknown job closes its run");
        Check(tracker.Finished[0].Status == JobRunStatuses.Failed, "an unknown job is recorded as failed");
        Check(tracker.Finished[0].Details?.Contains("gone.sync", StringComparison.Ordinal) == true,
            "the failure names the unknown job");
    }

    private static JobExecutionRunner Runner(JobCatalog catalog, RecordingTracker tracker, params IJobExecutor[] executors) =>
        new(catalog, executors, tracker, NullLogger<JobExecutionRunner>.Instance);

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }
}

internal static class JobSanitizerChecks
{
    public static void Run()
    {
        Check(JobDetailsSanitizer.Sanitize(null) is null, "absent details stay absent");

        // The message the operator quoted from the failing bundle sweep is not a secret and must survive:
        // sanitising cannot mean discarding the diagnosis.
        using var benign = JsonDocument.Parse(
            """{"error":"The current provider doesn't have a store type mapping for properties of type 'DBNull'."}""");
        var kept = JobDetailsSanitizer.Sanitize(benign.RootElement);
        Check(kept?.GetProperty("error").GetString()?.Contains("DBNull", StringComparison.Ordinal) == true,
            "a benign provider error is preserved");

        using var secret = JsonDocument.Parse(
            """{"error":"request failed","url":"https://api.gg.deals/v1/deals/?key=SECRET123&region=mx","nested":{"headers":["Bearer abc.def","Authorization: Bearer xyz"]},"password":"hunter2"}""");
        var sanitized = JobDetailsSanitizer.Sanitize(secret.RootElement)!.Value;
        var text = sanitized.GetRawText();
        Check(!text.Contains("SECRET123", StringComparison.Ordinal), "query credentials are redacted");
        Check(!text.Contains("abc.def", StringComparison.Ordinal), "bearer tokens are redacted");
        Check(!text.Contains("hunter2", StringComparison.Ordinal), "password values are redacted");
        Check(text.Contains("request failed", StringComparison.Ordinal), "the failure message survives");
        Check(text.Contains("region=mx", StringComparison.Ordinal), "non-secret query data survives");
        Check(sanitized.GetProperty("nested").GetProperty("headers").GetArrayLength() == 2,
            "the JSON shape is preserved");

        using var connection = JsonDocument.Parse(
            """{"error":"Npgsql: Host=db;Username=deals;Password=sup3r;Database=deals"}""");
        var connectionText = JobDetailsSanitizer.Sanitize(connection.RootElement)!.Value.GetRawText();
        Check(!connectionText.Contains("sup3r", StringComparison.Ordinal), "connection-string passwords are redacted");

        using var userInfo = JsonDocument.Parse("""{"source":"https://user:pw@example.test/path"}""");
        var userInfoText = JobDetailsSanitizer.Sanitize(userInfo.RootElement)!.Value.GetRawText();
        Check(!userInfoText.Contains("user:pw", StringComparison.Ordinal), "URL userinfo is redacted");
        Check(userInfoText.Contains("example.test", StringComparison.Ordinal), "the host stays readable");

        // B1: los detalles reales traen contadores numéricos y booleanos. Reutilizar los nodos del documento
        // de origen al reconstruir el objeto lanza "The node already has a parent" y convierte cualquier GET
        // de Jobs en un 500. El saneado debe clonar la forma conservada y no tocar el origen.
        using var counters = JsonDocument.Parse(
            """{"list":{"State":"ok","ItemCount":12,"Added":3,"Updated":9,"Removed":0,"FetchFailed":false},"pages":[1,2,3],"details":null}""");
        var countersRaw = counters.RootElement.GetRawText();
        var sanitizedCounters = JobDetailsSanitizer.Sanitize(counters.RootElement);
        Check(sanitizedCounters is not null, "los detalles con contadores sobreviven al saneado");
        var counterList = sanitizedCounters!.Value.GetProperty("list");
        Check(counterList.GetProperty("ItemCount").GetInt32() == 12, "un contador entero conserva valor y tipo");
        Check(counterList.GetProperty("Added").GetInt32() == 3, "el segundo entero conserva su valor");
        Check(counterList.GetProperty("Updated").GetInt32() == 9, "el tercer entero conserva su valor");
        Check(counterList.GetProperty("Removed").GetInt32() == 0, "un cero se conserva como 0, no se omite");
        Check(
            counterList.GetProperty("FetchFailed").ValueKind == JsonValueKind.False,
            "un booleano false se conserva como false");
        var pages = sanitizedCounters.Value.GetProperty("pages");
        Check(pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() == 3, "un array numérico se conserva");
        Check(pages[0].GetInt32() == 1 && pages[1].GetInt32() == 2 && pages[2].GetInt32() == 3, "los enteros del array conservan orden y valor");
        Check(
            sanitizedCounters.Value.GetProperty("details").ValueKind == JsonValueKind.Null,
            "un null se conserva como null");
        Check(counters.RootElement.GetRawText() == countersRaw, "el documento de origen no se muta");
        Check(
            counters.RootElement.GetProperty("list").GetProperty("ItemCount").GetInt32() == 12,
            "el origen sigue siendo legible después del saneado");

        // Un decimal conserva su forma numérica; una credencial numérica se redacta igual que una de texto.
        using var numeric = JsonDocument.Parse("""{"rate":18.42,"token":123456,"verified":true}""");
        var sanitizedNumeric = JobDetailsSanitizer.Sanitize(numeric.RootElement)!.Value;
        Check(sanitizedNumeric.GetProperty("rate").GetDouble() == 18.42, "un decimal conserva su valor");
        Check(
            sanitizedNumeric.GetProperty("token").ValueKind == JsonValueKind.String &&
            sanitizedNumeric.GetProperty("token").GetString() == "[redactado]",
            "una credencial numérica también se redacta");
        Check(
            sanitizedNumeric.GetProperty("verified").ValueKind == JsonValueKind.True,
            "un booleano true se conserva como true");

        // A runaway provider payload cannot become a megabyte in the admin table.
        var huge = new string('x', JobDetailsSanitizer.MaxStringLength + 500);
        using var longDocument = JsonDocument.Parse(JsonSerializer.Serialize(new { note = huge }));
        var truncated = JobDetailsSanitizer.Sanitize(longDocument.RootElement)!.Value.GetProperty("note").GetString();
        Check(truncated is not null && truncated.Length <= JobDetailsSanitizer.MaxStringLength + 3,
            "long strings are truncated");
        Console.WriteLine("PASS: jobs sanitizer checks");
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }
}

/// <summary>
/// PRESERVE: what this unit touched must still behave as before. Three things can silently regress here.
/// <list type="number">
/// <item>the scheduled wishlist cycle still refreshes prices — the manual default is the opposite (list
/// only), so inheriting it in the hosted service would quietly stop the daily price pass;</item>
/// <item>the scheduled ntfy cycle still runs both phases — an empty option set must mean "produce and
/// dispatch", which is what it did before it had options;</item>
/// <item><c>GET /api/jobs</c> keeps its legacy field names — the history page consumed them before the
/// filter, catalog and dispatch endpoints existed, and the new filter is additive.</item>
/// </list>
/// </summary>
internal static class JobCompatibilityChecks
{
    public static void Run()
    {
        Check(WishlistSyncJobExecutor.ScheduledOptions.IncludePriceRefresh,
            "the scheduled wishlist cycle still refreshes prices");
        Check(!JobRunOptions.Empty.IncludePriceRefresh,
            "the manual wishlist default stays list-only");
        Check(NtfyDealsJobExecutor.ScheduledOptions.ProduceAlerts &&
              NtfyDealsJobExecutor.ScheduledOptions.DispatchAlerts,
            "the scheduled ntfy cycle still produces and dispatches");
        Check(!JobRunOptions.Empty.DryRun, "the scheduled purge never starts as a simulation");

        // Every job the catalog offers must have exactly one executor, otherwise a manual run of a listed job
        // would be accepted and then fail at dispatch time with 'no executor'.
        var executorJobs = new[]
        {
            WishlistSyncJobExecutor.JobKey,
            FxRateRefreshJobExecutor.JobKey,
            GlobalBundleSyncJobExecutor.JobKey,
            GgDealsBundleSyncJobExecutor.JobKey,
            GlobalDealSyncJobExecutor.JobKey,
            NtfyDealsJobExecutor.JobKey,
            JobRunPurgeJobExecutor.JobKey
        };
        Check(executorJobs.Distinct(StringComparer.Ordinal).Count() == executorJobs.Length,
            "each job key has one executor");
        Check(executorJobs.ToHashSet(StringComparer.Ordinal).SetEquals(JobKeys.All),
            "the catalog and the executors describe the same seven jobs");

        Check(JobRunTracker.AbandonedAfter == TimeSpan.FromHours(6),
            "the abandoned-run window is the documented six hours");

        var item = new JobRunListItemResponse(
            1,
            "jobs.purge",
            "manual",
            "ok",
            new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 19, 10, 0, 1, DateTimeKind.Utc),
            1000,
            null);
        var page = new JobRunHistoryResponse(
            new JobRunSummaryResponse(1, 0, 1, 0, item.StartedAt),
            [item],
            1,
            25,
            1,
            new JobRunFilterResponse("jobs.purge", null, null));
        // The API serialises with the web defaults (camelCase); the check pins the names the page consumed.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(page, web));
        var root = document.RootElement;
        foreach (var legacy in new[] { "summary", "items", "page", "pageSize", "totalPages" })
        {
            Check(root.TryGetProperty(legacy, out _), $"GET /api/jobs keeps '{legacy}'");
        }

        Check(root.TryGetProperty("filters", out _), "the applied filter is additive");
        var run = root.GetProperty("items")[0];
        foreach (var legacy in new[] { "jobRunId", "job", "trigger", "status", "startedAt", "finishedAt", "durationMilliseconds", "details" })
        {
            Check(run.TryGetProperty(legacy, out _), $"a run keeps '{legacy}'");
        }

        using var queued = JsonDocument.Parse(JsonSerializer.Serialize(
            new JobRunQueuedResponse(9, "fx.rate-refresh", "running", item.StartedAt),
            web));
        foreach (var field in new[] { "jobRunId", "job", "status", "requestedAt" })
        {
            Check(queued.RootElement.TryGetProperty(field, out _), $"a manual dispatch answers '{field}'");
        }

        Console.WriteLine("PASS: jobs compatibility checks");
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
    }
}
