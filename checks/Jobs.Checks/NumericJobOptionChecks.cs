#nullable enable
using System.Text.Json;
using Deals.API.Models.Jobs;
using Deals.API.Services.Jobs;
using Deals.BusinessLogic.Models.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobsChecks;

/// <summary>
/// Spec S2/S3/S5: the manual <c>catalog.prices-refresh</c> job takes ONE numeric option — how many hours of price
/// age the requester wants re-priced — and every other job keeps its boolean option semantics unchanged.
///
/// The contract is pinned at the JSON boundary on purpose: the admin page reads the catalog response, not these
/// records, so "the option is an integer between 1 and 720 whose defaultValue is the number 72" has to be true of
/// what is serialised. The additive rule is checked in both directions: a new <c>type</c>/<c>minValue</c>/
/// <c>maxValue</c> must appear for the integer option, a boolean option must keep working exactly as before, and a
/// numeric value must never be accepted for a switch (or for a job that declares no options).
///
/// Failures are collected instead of thrown on the first one: this unit touches the descriptor, the parser, the run
/// details and the submission path, and a single missing layer must not hide the state of the others.
/// </summary>
internal static class NumericJobOptionChecks
{
    private const string AgeHoursKey = "ageHours";
    private const string PricePass = "catalog.prices-refresh";
    private const string WishlistJob = "wishlist.sync";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly List<string> Failures = [];

    public static async Task RunAsync()
    {
        Section(DescriptorContract);
        Section(WholeCatalogContract);
        Section(ApiResponseContract);
        Section(AcceptsBoundedIntegers);
        Section(RejectsInvalidIntegers);
        Section(OtherJobsStayBoolean);
        await SectionAsync(RejectsInvalidAgeBeforeAnyWriteAsync);
        await SectionAsync(StartDetailsCarryTheChosenWindowAsync);
        await SectionAsync(ExecutorPropagatesChosenWindowAsync);

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: {Failures.Count} numeric job option checks failed");
            foreach (var failure in Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            Environment.Exit(1);
        }

        Console.WriteLine("PASS: numeric job option checks");
    }

    /// <summary>
    /// The descriptor of the price pass: one option, the documented key, the integer type, the 1..720 bounds and the
    /// 72-hour default. The label is the one the user asked for, and the help text has to say two things the user
    /// cannot infer: it is the requester's own wishlist, and it is independent of the fixed 72 h monitor.
    /// </summary>
    private static void DescriptorContract()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);
        var entry = catalog.Find(PricePass)!;

        CheckEqual(1, entry.Options.Count, "the price pass exposes exactly one bounded option");
        if (entry.Options.Count != 1)
        {
            return;
        }

        var option = entry.Options[0];

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(option, Web));
        var root = document.RootElement;

        CheckEqual(AgeHoursKey, root.GetProperty("key").GetString(), "the option key is ageHours");
        CheckEqual("Antigüedad mínima (horas)", root.GetProperty("label").GetString(),
            "the option label names hours");
        CheckEqual("integer", root.GetProperty("type").GetString(),
            "the option declares its integer type instead of relying on a missing field");
        CheckEqual(JsonValueKind.Number, root.GetProperty("defaultValue").ValueKind,
            "the default is a number, not a boolean");
        CheckEqual(72, root.GetProperty("defaultValue").GetInt32(), "the default window is 72 hours");
        CheckEqual(1, root.GetProperty("minValue").GetInt32(), "the smallest accepted window is 1 hour");
        CheckEqual(720, root.GetProperty("maxValue").GetInt32(), "the largest accepted window is 720 hours");

        var help = root.GetProperty("help").GetString() ?? string.Empty;
        Check(help.Contains("deseados", StringComparison.OrdinalIgnoreCase),
            "the help names the requester's own wishlist");
        Check(
            help.Contains("no existe", StringComparison.OrdinalIgnoreCase) ||
            help.Contains("sin precio", StringComparison.OrdinalIgnoreCase),
            "the help says missing prices are included");
        Check(help.Contains("monitor", StringComparison.OrdinalIgnoreCase),
            "the help says the window is independent of the fixed monitor");

        // The batch bound stays a constant, not an option: an operator cannot widen it through the payload.
        Check(!entry.Options.Any(candidate => candidate.Key.Contains("batch", StringComparison.OrdinalIgnoreCase)),
            "no option widens the batch");
        CheckEqual(20, CatalogPriceRefreshLimits.BatchSize,
            "the batch is still hard bounded to 20 games");
    }

    /// <summary>
    /// The whole catalog, not only the price pass: exactly ONE integer option exists (the price pass window), every
    /// other option is a boolean with no bounds, and no descriptor declares an unknown type. This is what keeps the
    /// additive contract from leaking a second numeric option into a job that never asked for one.
    /// </summary>
    private static void WholeCatalogContract()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);
        var options = catalog.All.SelectMany(entry => entry.Options).ToList();

        var integers = options
            .Where(option => string.Equals(option.Type, JobOptionTypes.Integer, StringComparison.Ordinal))
            .ToList();
        CheckEqual(1, integers.Count, "the catalog has exactly one integer option");
        CheckEqual(AgeHoursKey, string.Join(",", integers.Select(option => option.Key)),
            "the only integer option is the price-pass window");

        var owners = catalog.All
            .Where(entry => entry.Options.Any(option => string.Equals(option.Type, JobOptionTypes.Integer, StringComparison.Ordinal)))
            .Select(entry => entry.Job)
            .ToList();
        CheckEqual(PricePass, string.Join(",", owners), "only the price pass owns the integer option");

        foreach (var option in options)
        {
            Check(
                option.Type is JobOptionTypes.Boolean or JobOptionTypes.Integer,
                $"option {option.Key} declares a known type");

            if (string.Equals(option.Type, JobOptionTypes.Boolean, StringComparison.Ordinal))
            {
                Check(option.DefaultValue is bool, $"boolean option {option.Key} defaults to a boolean");
                Check(option.MinValue is null, $"boolean option {option.Key} declares no minimum");
                Check(option.MaxValue is null, $"boolean option {option.Key} declares no maximum");
            }
        }
    }

    /// <summary>
    /// The catalog response is the contract the admin page consumes. A boolean option keeps its old fields and gains
    /// an explicit <c>boolean</c> type; the new numeric option carries the number 72 and its bounds so the page can
    /// render an hours input without knowing the job. <c>minValue</c>/<c>maxValue</c> are absent for switches, which
    /// is what "optional" means for the frozen contract.
    /// </summary>
    private static void ApiResponseContract()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);
        var pricePass = catalog.Find(PricePass)!;
        if (pricePass.Options.Count != 1)
        {
            Check(false, "the numeric option is missing, so the response cannot carry it");
            return;
        }

        var integer = JobOptionResponse.From(pricePass.Options[0]);
        using var integerDocument = JsonDocument.Parse(JsonSerializer.Serialize(integer, Web));
        var integerRoot = integerDocument.RootElement;
        CheckEqual(AgeHoursKey, integerRoot.GetProperty("key").GetString(), "the response keeps the key");
        CheckEqual("integer", integerRoot.GetProperty("type").GetString(), "the response carries the integer type");
        CheckEqual(72, integerRoot.GetProperty("defaultValue").GetInt32(),
            "the response carries the numeric default");
        CheckEqual(1, integerRoot.GetProperty("minValue").GetInt32(), "the response carries the minimum");
        CheckEqual(720, integerRoot.GetProperty("maxValue").GetInt32(), "the response carries the maximum");

        // A switch, as it was answered before: same field names, and the additive type says boolean.
        var boolean = JobOptionResponse.From(catalog.Find(WishlistJob)!.Options[0]);
        using var booleanDocument = JsonDocument.Parse(JsonSerializer.Serialize(boolean, Web));
        var booleanRoot = booleanDocument.RootElement;
        CheckEqual("includePriceRefresh", booleanRoot.GetProperty("key").GetString(), "the switch key is kept");
        CheckEqual("boolean", booleanRoot.GetProperty("type").GetString(), "a switch says it is a boolean");
        CheckEqual(JsonValueKind.False, booleanRoot.GetProperty("defaultValue").ValueKind,
            "a switch keeps its boolean default, not 0");
        Check(!booleanRoot.TryGetProperty("minValue", out _), "a switch has no minimum");
        Check(!booleanRoot.TryGetProperty("maxValue", out _), "a switch has no maximum");
    }

    /// <summary>
    /// Both boundary values and the documented default are accepted, and the chosen number reaches the run's
    /// details as a JSON number. A value stored as a string would read as "72" in the history and could not be
    /// compared with the window the pass actually used, so the type is asserted, not just the value.
    /// </summary>
    private static void AcceptsBoundedIntegers()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        var absent = Parse(catalog, PricePass, null);
        Check(absent.Succeeded, "absent options fall back to the documented default");
        CheckEqual(72, AgeHoursOf(absent), "an absent ageHours means 72 hours");

        foreach (var hours in new[] { 1, 24, 72, 168, 720 })
        {
            var parsed = Parse(catalog, PricePass, $$"""{"{{AgeHoursKey}}": {{hours}}}""");
            Check(parsed.Succeeded, $"ageHours {hours} is accepted");
            if (!parsed.Succeeded)
            {
                Check(false, $"ageHours {hours} was refused with: {parsed.Error}");
                continue;
            }

            CheckEqual(hours, AgeHoursOf(parsed), $"ageHours {hours} survives parsing");

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed.Options.ToDetails(), Web));
            var value = document.RootElement.GetProperty(AgeHoursKey);
            CheckEqual(JsonValueKind.Number, value.ValueKind, $"ageHours {hours} is recorded as a JSON number");
            CheckEqual(hours, value.GetInt32(), $"ageHours {hours} keeps its value in the run details");
        }
    }

    /// <summary>
    /// Everything that is not a bounded integer is refused by name: out of range on either side, a fraction, a
    /// string, a boolean, null, and a non-number container. A refusal that does not name the option would leave the
    /// operator guessing which field the page sent.
    /// </summary>
    private static void RejectsInvalidIntegers()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        foreach (var (payload, why) in new[]
        {
            ("""{"ageHours": 0}""", "zero hours is below the minimum"),
            ("""{"ageHours": -3}""", "a negative window is not a window"),
            ("""{"ageHours": 721}""", "721 hours is above the maximum"),
            ("""{"ageHours": 1.5}""", "a fractional window is not an integer"),
            ("""{"ageHours": "24"}""", "a numeric string is not a number"),
            ("""{"ageHours": true}""", "true is not a number"),
            ("""{"ageHours": false}""", "false is not a number"),
            ("""{"ageHours": null}""", "null is not a number"),
            ("""{"ageHours": [24]}""", "an array is not a number"),
            ("""{"ageHours": {}}""", "an object is not a number"),
            ("""{"ageHours": 1e2}""", "a scientific literal is not an integer literal"),
            ("""{"ageHours": 201.0}""", "a decimal literal is not an integer literal"),
            ("""{"ageHours": 99999999999999999999}""", "a huge number is not an int32"),
            ("""{"ageHours": 1E+2}""", "an exponent literal is not an integer literal")
        })
        {
            var parsed = Parse(catalog, PricePass, payload);
            Check(!parsed.Succeeded, why);
            Check(parsed.Error?.Contains(AgeHoursKey, StringComparison.Ordinal) == true,
                $"{why}: the error names the option");
            Check(parsed.Error?.Contains("entero", StringComparison.OrdinalIgnoreCase) == true,
                $"{why}: the error says an integer is expected");
        }

        var unknown = Parse(catalog, PricePass, """{"includeMissing": true}""");
        Check(!unknown.Succeeded, "an option that does not exist is refused, not ignored");
        Check(unknown.Error?.Contains("includeMissing", StringComparison.Ordinal) == true,
            "the unknown option is named");

        // A valid window does not excuse an unknown key: the payload is refused whole, so no part of it can be
        // silently dropped while the operator believes it applied.
        var mixedUnknown = Parse(catalog, PricePass, """{"ageHours": 24, "includeMissing": true}""");
        Check(!mixedUnknown.Succeeded, "an unknown key next to a valid window is still refused");
        Check(mixedUnknown.Error?.Contains("includeMissing", StringComparison.Ordinal) == true,
            "the unknown key next to a valid window is named");

        // The payload envelope itself: an array, a string and a bare number are not option objects.
        foreach (var (payload, why) in new[]
        {
            ("""[{"ageHours": 24}]""", "an array envelope is not an options object"),
            ("\"ageHours=24\"", "a string envelope is not an options object"),
            ("""24""", "a bare number envelope is not an options object"),
            ("""true""", "a bare boolean envelope is not an options object")
        })
        {
            var enveloped = Parse(catalog, PricePass, payload);
            Check(!enveloped.Succeeded, why);
            Check(enveloped.Error is not null, $"{why}: the refusal carries a message");
        }
    }

    /// <summary>
    /// PRESERVE: the other jobs keep their boolean options exactly. A number is not a switch, an option of another
    /// job is not accepted here, and the switch that used to work still works and still defaults the same way.
    /// </summary>
    private static void OtherJobsStayBoolean()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        foreach (var (job, payload) in new[]
        {
            (WishlistJob, """{"includePriceRefresh": 1}"""),
            (WishlistJob, """{"includePriceRefresh": 0}"""),
            (WishlistJob, """{"includePriceRefresh": 24}"""),
            (JobKeys.NtfyAlerts, """{"produce": 1}"""),
            (JobKeys.NtfyAlerts, """{"produce": 3}"""),
            (JobKeys.NtfyAlerts, """{"dispatch": 0}"""),
            (JobKeys.NtfyAlerts, """{"dispatch": 2}"""),
            (JobKeys.Purge, """{"dryRun": 1}"""),
            (JobKeys.Purge, """{"dryRun": 0}""")
        })
        {
            var parsed = Parse(catalog, job, payload);
            Check(!parsed.Succeeded, $"{job} refuses {payload} as a value");
            Check(parsed.Error?.Contains("true o false", StringComparison.Ordinal) == true,
                $"{job} keeps explaining that a switch is true or false");
        }

        var ageOnWishlist = Parse(catalog, WishlistJob, """{"ageHours": 24}""");
        Check(!ageOnWishlist.Succeeded, "the numeric option does not leak into another job");
        Check(ageOnWishlist.Error?.Contains(AgeHoursKey, StringComparison.Ordinal) == true,
            "the rejected option is named");

        var ageOnOptionless = Parse(catalog, JobKeys.GlobalBundles, """{"ageHours": 24}""");
        Check(!ageOnOptionless.Succeeded, "a job with no options still refuses every option");

        var wishlist = Parse(catalog, WishlistJob, """{"includePriceRefresh": true}""");
        Check(wishlist.Succeeded && wishlist.Options.IncludePriceRefresh,
            "the wishlist switch still applies when it is a real boolean");

        var ntfy = Parse(catalog, JobKeys.NtfyAlerts, """{"dispatch": false}""");
        Check(ntfy.Succeeded && ntfy.Options.ProduceAlerts && !ntfy.Options.DispatchAlerts,
            "one ntfy switch still does not reset the other");
    }

    /// <summary>
    /// Spec S2: an invalid window is refused <em>before</em> the queue is reserved or a run row is created. Both are
    /// observed (reservation calls and started rows), so a refusal that happened after a write would fail here.
    /// </summary>
    private static async Task RejectsInvalidAgeBeforeAnyWriteAsync()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        foreach (var payload in new[]
        {
            """{"ageHours": 0}""",
            """{"ageHours": 721}""",
            """{"ageHours": 1.5}""",
            """{"ageHours": "72"}""",
            """{"ageHours": null}"""
        })
        {
            var queue = new FakeJobRunQueue();
            var tracker = new RecordingTracker();
            var submissions = new JobSubmissionService(
                catalog, queue, tracker, NullLogger<JobSubmissionService>.Instance);

            using var options = JsonDocument.Parse(payload);
            var outcome = await submissions.SubmitAsync(
                PricePass, options.RootElement, false, CancellationToken.None, requestedByUserId: 42);

            Check(outcome.Status == JobSubmitStatus.Invalid, $"{payload} is refused as invalid");
            CheckEqual(0, queue.ReserveCalls, $"{payload} never reserves the queue");
            CheckEqual(0, tracker.Started.Count, $"{payload} never creates a run row");
        }

        // Positive control: the same path with a valid window is accepted, so the refusals above are not passing
        // because submission is broken for every payload.
        var acceptingQueue = new FakeJobRunQueue();
        var acceptingTracker = new RecordingTracker();
        var accepting = new JobSubmissionService(
            catalog, acceptingQueue, acceptingTracker, NullLogger<JobSubmissionService>.Instance);

        using var valid = JsonDocument.Parse("""{"ageHours": 24}""");
        var accepted = await accepting.SubmitAsync(
            PricePass, valid.RootElement, false, CancellationToken.None, requestedByUserId: 42);

        Check(accepted.IsSuccess, "a valid window is accepted");
        CheckEqual(1, acceptingQueue.Published.Count, "the accepted run reaches the queue exactly once");
        if (acceptingQueue.Published.Count == 1)
        {
            using var travelling = JsonDocument.Parse(
                JsonSerializer.Serialize(acceptingQueue.Published[0].Options.ToDetails(), Web));
            CheckEqual(24, travelling.RootElement.GetProperty(AgeHoursKey).GetInt32(),
                "the chosen window travels with the run");
        }
    }

    /// <summary>
    /// Spec S3: the executor hands the chosen window to the pass and repeats it in the run record, together with the
    /// two pending split counters. A run nobody parameterised uses the documented default instead of an arbitrary
    /// number, and the record shows hours as a number, never as "sí/no".
    /// </summary>
    private static async Task ExecutorPropagatesChosenWindowAsync()
    {
        var chosen = new RecordingAgeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            Pending: 4, Selected: 2, Updated: 2, Skipped: 0, GateDeclined: 0, NoComparablePrice: 0,
            Unsupported: 0, NotDue: 2, Degraded: 0, Errors: 0, Remaining: 1,
            AgeHours: 24, OlderPricePending: 3, MissingPricePending: 1));

        var result = await new CatalogPriceRefreshJobExecutor(chosen).ExecuteAsync(
            new JobRunOptions(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JobOptionKeys.AgeHours] = 24
            }),
            requestedByUserId: 42,
            CancellationToken.None);

        CheckEqual("24", string.Join(",", chosen.AgeHours), "the chosen window reaches the pass");
        var details = Details(result);
        CheckEqual(JsonValueKind.Number, details.GetProperty("ageHours").ValueKind,
            "the record keeps the window as a number");
        CheckEqual(24, details.GetProperty("ageHours").GetInt32(), "the record shows the chosen hours");
        CheckEqual(3, details.GetProperty("olderPricePending").GetInt32(),
            "the record separates the old-price pending games");
        CheckEqual(1, details.GetProperty("missingPricePending").GetInt32(),
            "the record separates the missing-price pending games");
        CheckEqual(4, details.GetProperty("pending").GetInt32(), "the total pending count is still reported");

        // PRESERVE: a run with no options (the shape every scheduled cycle uses) is the documented default.
        var byDefault = new RecordingAgeCatalogPriceRefreshService(new CatalogPriceRefreshOutcome(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        await new CatalogPriceRefreshJobExecutor(byDefault).ExecuteAsync(
            JobRunOptions.Empty, requestedByUserId: 42, CancellationToken.None);
        CheckEqual("72", string.Join(",", byDefault.AgeHours),
            "an unparameterised run uses the documented 72 h default");
    }

    /// <summary>
    /// Spec S3/S4: the numeric options are persisted in the START details too, not only when the run finishes. A
    /// queued row, a run that is still working and a row whose process died mid-run only ever have
    /// <c>job_runs.details</c> from <c>StartAsync</c>, so without the options there the record loses the window that
    /// produced its numbers. The requester-scoped price pass is the only job whose start details carry anything;
    /// every other job's baseline stays null.
    /// </summary>
    private static async Task StartDetailsCarryTheChosenWindowAsync()
    {
        var catalog = new JobCatalog(JobCatalogSettings.Default);

        var chosenTracker = new RecordingTracker();
        var chosen = new JobSubmissionService(
            catalog, new FakeJobRunQueue(), chosenTracker, NullLogger<JobSubmissionService>.Instance);
        using var chosenOptions = JsonDocument.Parse("""{"ageHours": 168}""");
        var chosenOutcome = await chosen.SubmitAsync(
            PricePass, chosenOptions.RootElement, false, CancellationToken.None, requestedByUserId: 42);

        Check(chosenOutcome.IsSuccess, "a 168 h request is accepted");
        CheckEqual(1, chosenTracker.StartedDetails.Count, "the accepted run started exactly one row");
        var chosenStart = StartDetails(chosenTracker.StartedDetails[0]);
        CheckEqual(42, chosenStart.GetProperty("requestedBy").GetInt32(),
            "the start record still carries the requester");
        Check(chosenStart.TryGetProperty("requested", out var chosenRequested),
            "the start record carries the requested options");
        CheckEqual(JsonValueKind.Number, chosenRequested.GetProperty(AgeHoursKey).ValueKind,
            "the start record keeps the window as a JSON number, not a string");
        CheckEqual(168, chosenRequested.GetProperty(AgeHoursKey).GetInt32(),
            "the start record keeps the chosen 168 h window");

        // The documented default is persisted numerically too, so an unparameterised row cannot be read as "no
        // choice made" while the run used 72 h.
        var defaultTracker = new RecordingTracker();
        var defaultRun = new JobSubmissionService(
            catalog, new FakeJobRunQueue(), defaultTracker, NullLogger<JobSubmissionService>.Instance);
        var defaultOutcome = await defaultRun.SubmitAsync(
            PricePass, null, false, CancellationToken.None, requestedByUserId: 42);

        Check(defaultOutcome.IsSuccess, "a request without options is accepted");
        var defaultStart = StartDetails(defaultTracker.StartedDetails[0]);
        CheckEqual(JsonValueKind.Number, defaultStart.GetProperty("requested").GetProperty(AgeHoursKey).ValueKind,
            "the default window is recorded as a JSON number");
        CheckEqual(72, defaultStart.GetProperty("requested").GetProperty(AgeHoursKey).GetInt32(),
            "the default window recorded is 72 h");

        // PRESERVE: no other job gains start details. Their baseline was null and stays null.
        foreach (var (job, payload) in new[]
        {
            (WishlistJob, """{"includePriceRefresh": true}"""),
            (JobKeys.FxRateRefresh, null),
            (JobKeys.GlobalBundles, null)
        })
        {
            var tracker = new RecordingTracker();
            var submissions = new JobSubmissionService(
                catalog, new FakeJobRunQueue(), tracker, NullLogger<JobSubmissionService>.Instance);
            using var options = payload is null ? null : JsonDocument.Parse(payload);
            var outcome = await submissions.SubmitAsync(
                job, options?.RootElement, false, CancellationToken.None);

            Check(outcome.IsSuccess, $"{job} is still accepted");
            CheckEqual(1, tracker.StartedDetails.Count, $"{job} still starts one row");
            Check(tracker.StartedDetails[0] is null, $"{job} keeps its option-free start details");
        }

        // The refusal path is unchanged: an invalid window still writes no row at all.
        var refusedTracker = new RecordingTracker();
        var refused = new JobSubmissionService(
            catalog, new FakeJobRunQueue(), refusedTracker, NullLogger<JobSubmissionService>.Instance);
        using var invalidOptions = JsonDocument.Parse("""{"ageHours": 721}""");
        var refusedOutcome = await refused.SubmitAsync(
            PricePass, invalidOptions.RootElement, false, CancellationToken.None, requestedByUserId: 42);

        CheckEqual(JobSubmitStatus.Invalid, refusedOutcome.Status, "an invalid window is still refused");
        CheckEqual(0, refusedTracker.StartedDetails.Count, "an invalid window still writes no start row");
    }

    // --- Helpers ----------------------------------------------------------

    private static JsonElement StartDetails(string? serialized)
    {
        Check(serialized is not null, "the run was expected to record start details");
        using var document = JsonDocument.Parse(serialized ?? "{}");
        return document.RootElement.Clone();
    }

    private static JsonElement Details(JobExecutionResult result)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(result.Details, Web));
        return document.RootElement.Clone();
    }

    private static JobOptionParseResult Parse(JobCatalog catalog, string job, string? options)
    {
        using var document = options is null ? null : JsonDocument.Parse(options);
        return JobOptionParser.Parse(catalog.Find(job)!, document?.RootElement, confirm: true);
    }

    /// <summary>Reads the effective window the way the run record carries it: through the serialised details.</summary>
    private static int AgeHoursOf(JobOptionParseResult parsed)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed.Options.ToDetails(), Web));
        return document.RootElement.GetProperty(AgeHoursKey).GetInt32();
    }

    private static void Section(Action section)
    {
        try
        {
            section();
        }
        catch (Exception exception)
        {
            Failures.Add($"{section.Method.Name}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task SectionAsync(Func<Task> section)
    {
        try
        {
            await section();
        }
        catch (Exception exception)
        {
            Failures.Add($"{section.Method.Name}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Check(bool value, string name)
    {
        if (!value) Failures.Add(name);
    }

    private static void CheckEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Failures.Add($"{name}: expected {expected}, got {actual}");
    }

    /// <summary>
    /// Pass double that records the window it received, so "the chosen hours reach the pass" is an observation and
    /// not a promise. It never touches a provider: the mapping from options to the pass is what is under test.
    /// </summary>
    private sealed class RecordingAgeCatalogPriceRefreshService(CatalogPriceRefreshOutcome outcome)
        : Deals.BusinessLogic.Interfaces.ICatalogPriceRefreshService
    {
        public List<int> AgeHours { get; } = [];

        public Task<CatalogPriceRefreshOutcome> RefreshPendingAsync(
            int requestedByUserId,
            CancellationToken cancellationToken = default,
            int ageHours = CatalogPriceRefreshLimits.DefaultAgeHours)
        {
            AgeHours.Add(ageHours);
            return Task.FromResult(outcome);
        }
    }
}
