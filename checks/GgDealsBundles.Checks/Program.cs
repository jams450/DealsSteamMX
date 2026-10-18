using System.Net;
using System.Text;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.GgDeals;
using Deals.BusinessLogic.Models.Itad;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using Microsoft.Extensions.Logging.Abstractions;

// Deterministic, no-database checks for the gg.deals active bundle integration (T6, S4).
//
// Three layers are exercised here:
//   1. The public GgDealsClient active-catalog parser against a fake HTTP handler: valid page, nullable
//      URL, malformed root/entry, historical date, selection count, cursor validation, 429 retry,
//      transport failure, one-record governor accounting and the no-secret-in-errors guarantee.
//   2. The pure GgDealsBundleSweep decision: completion, repeated cursor/page, total mismatch, page
//      budget, cursor expiry and page failure, plus the service apply-only-on-complete rule.
//   3. The mapper and source isolation: gg.deals metadata, selection bound, and ITAD defaults preserved.
//
// Every fixture is synthetic and marked as such; no real provider is contacted and no credential is
// used beyond a clearly fake `synthetic-key`.

var failures = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        failures.Add(name);
    }
}

async Task CheckThrows<TException>(Func<Task> action, string name, Func<TException, bool>? predicate = null)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        if (predicate is null || predicate(exception))
        {
            return;
        }

        failures.Add($"{name} (exception did not satisfy the predicate)");
        return;
    }
    catch (Exception exception)
    {
        failures.Add($"{name} (threw {exception.GetType().Name} instead of {typeof(TException).Name})");
        return;
    }

    failures.Add($"{name} (no exception)");
}

static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
{
    var response = new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    return response;
}

const string SyntheticKey = "synthetic-key";
const string SyntheticNext = "https://api.gg.deals/v1/bundles/active/?key=synthetic-key&region=us&offset=100";

// A schema-shaped active page: one active bundle, one tier with a choose-2-of-2 rule. Not a recorded
// response; the ids, titles and figures are invented for the check.
static string ActivePageJson(string next, int totalCount, string bundles = "")
{
    var nextLiteral = next.Length == 0 ? "null" : $"\"{next}\"";
    return $$"""
    {
      "success": true,
      "data": {
        "previous": null,
        "next": {{nextLiteral}},
        "totalCount": {{totalCount}},
        "bundles": [{{bundles}}]
      }
    }
    """;
}

const string SyntheticBundle = """
{
  "title": "Synthetic Bundle",
  "url": "https://gg.deals/bundle/synthetic/",
  "dateFrom": "2026-10-01 00:00:00",
  "dateTo": null,
  "tiers": [
    {
      "price": "19.99",
      "currency": "usd",
      "gamesCount": 2,
      "games": [
        { "title": "Game A", "url": "https://gg.deals/game/a/" },
        { "title": "Game B", "url": null }
      ]
    }
  ]
}
""";

const string NullUrlBundle = """
{
  "title": "Synthetic Without Url",
  "url": null,
  "dateFrom": "2026-10-01 00:00:00",
  "dateTo": null,
  "tiers": [ { "price": null, "currency": null, "gamesCount": null, "games": [] } ]
}
""";

const string HistoricalBundle = """
{
  "title": "Synthetic Historical",
  "url": "https://gg.deals/bundle/old/",
  "dateFrom": "2020-01-01 00:00:00",
  "dateTo": "2020-02-01 00:00:00",
  "tiers": [ { "price": "1.00", "currency": "USD", "gamesCount": null, "games": [] } ]
}
""";

static GgDealsClient CreateClient(
    FakeHttpHandler handler,
    GgDealsRequestGovernor governor,
    out HttpClient http)
{
    http = new HttpClient(handler) { BaseAddress = new Uri("https://api.gg.deals/v1/") };
    var settings = new GgDealsClientSettings(SyntheticKey, "us");
    return new GgDealsClient(http, settings, governor, NullLogger<GgDealsClient>.Instance);
}

static GgDealsRequestGovernor CreateGovernor(int perMinute = 1000) =>
    new(perMinute, Math.Max(perMinute, 1000), 100, 0);

// --- A. Public client parser against a fake HTTP handler ---------------------

// A valid page parses every documented field.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(ActivePageJson(SyntheticNext, 1, SyntheticBundle)));
    var governor = CreateGovernor();
    var client = CreateClient(handler, governor, out var http);

    var page = await client.GetActiveBundlesAsync(null, CancellationToken.None);
    Check(page.Bundles.Count == 1, "the active page parses its bundle");
    var bundle = page.Bundles[0];
    Check(bundle.Title == "Synthetic Bundle", "the bundle title is preserved");
    Check(bundle.Url == "https://gg.deals/bundle/synthetic/", "the nullable article URL is preserved");
    Check(bundle.Tiers.Count == 1, "the tier is parsed");
    Check(bundle.Tiers[0].PriceMinor == 1999, "the decimal-string price is converted to minor units");
    Check(bundle.Tiers[0].Currency == "USD", "the currency is normalized to uppercase");
    Check(bundle.Tiers[0].GamesCount == 2, "the choose-N selection count is parsed");
    Check(bundle.Tiers[0].Games.Count == 2, "both listed games survive");
    Check(bundle.Tiers[0].Games[1].Url is null, "a null item URL is valid");
    Check(bundle.DateFrom.Kind == DateTimeKind.Utc, "the start date is UTC");
    Check(bundle.DateTo is null, "a null end date is valid");
    Check(bundle.BundleKey.StartsWith(GgDealsBundleKey.Prefix, StringComparison.Ordinal),
        "the heuristic bundle key carries its versioned prefix");
    Check(page.TotalCount == 1, "the provider total is preserved");
    Check(page.NextCursor == SyntheticNext, "the validated next cursor is returned");
    Check(handler.Requests.Count == 1 &&
          handler.Requests[0].AbsolutePath == "/v1/bundles/active/" &&
          handler.Requests[0].Query.Contains("key=synthetic-key") &&
          handler.Requests[0].Query.Contains("region=us"),
        "the first request hits the exact provider path with the key and region");
    http.Dispose();
    governor.Dispose();
}

// A later call follows the returned cursor verbatim.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(ActivePageJson("", 0)));
    var governor = CreateGovernor();
    var client = CreateClient(handler, governor, out var http);
    await client.GetActiveBundlesAsync(SyntheticNext, CancellationToken.None);
    Check(handler.Requests.Single().AbsoluteUri == SyntheticNext,
        "the returned cursor is followed verbatim");
    http.Dispose();
    governor.Dispose();
}

// Null URL and null price/currency/gamesCount are all valid.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(ActivePageJson("", 1, NullUrlBundle)));
    var governor = CreateGovernor();
    var client = CreateClient(handler, governor, out var http);
    var page = await client.GetActiveBundlesAsync(null, CancellationToken.None);
    Check(page.Bundles.Count == 1 && page.Bundles[0].Url is null, "a null article URL is a valid bundle");
    Check(page.Bundles[0].Tiers[0].PriceMinor is null && page.Bundles[0].Tiers[0].Currency is null,
        "a build-your-own tier keeps its null price and currency");
    http.Dispose();
    governor.Dispose();
}

// A historical bundle is dropped, not kept as active.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(ActivePageJson("", 1, HistoricalBundle)));
    var governor = CreateGovernor();
    var client = CreateClient(handler, governor, out var http);
    var page = await client.GetActiveBundlesAsync(null, CancellationToken.None);
    Check(page.Bundles.Count == 0, "a bundle whose end date is in the past is not active");
    http.Dispose();
    governor.Dispose();
}

// Malformed envelopes throw loudly and never echo the key.
{
    foreach (var (body, name) in new[]
    {
        ("[]", "a non-object root is rejected"),
        ("{\"success\": false, \"data\": {}}", "an unsuccessful envelope is rejected"),
        ("{\"success\": true, \"data\": {\"totalCount\": 0}}", "a missing bundles array is rejected"),
        ("{\"success\": true, \"data\": {\"totalCount\": -1, \"bundles\": []}}", "a negative total is rejected")
    })
    {
        var handler = new FakeHttpHandler(_ => JsonResponse(body));
        var governor = CreateGovernor();
        var client = CreateClient(handler, governor, out var http);
        await CheckThrows<System.Text.Json.JsonException>(
            () => client.GetActiveBundlesAsync(null, CancellationToken.None),
            name,
            exception => !exception.Message.Contains("synthetic-key", StringComparison.Ordinal) &&
                         !exception.Message.Contains("http", StringComparison.OrdinalIgnoreCase));
        http.Dispose();
        governor.Dispose();
    }
}

// A non-object bundle entry or a non-array tier list fails the page.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(ActivePageJson("", 1, "42")));
    var governor = CreateGovernor();
    var client = CreateClient(handler, governor, out var http);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetActiveBundlesAsync(null, CancellationToken.None),
        "a non-object bundle entry is rejected");
    http.Dispose();
    governor.Dispose();
}

// Invalid cursor links are rejected before they can carry the key elsewhere.
{
    foreach (var (cursor, name) in new[]
    {
        ("http://api.gg.deals/v1/bundles/active/?key=synthetic-key", "an http cursor is rejected"),
        ("https://evil.example/v1/bundles/active/?key=synthetic-key", "a foreign host cursor is rejected"),
        ("https://api.gg.deals/v1/other/?key=synthetic-key", "a wrong path cursor is rejected"),
        ("https://user@api.gg.deals/v1/bundles/active/?key=synthetic-key", "a userinfo cursor is rejected"),
        ("https://api.gg.deals/v1/bundles/active/?key=synthetic-key#frag", "a fragment cursor is rejected"),
        ("https://api.gg.deals:8443/v1/bundles/active/?key=synthetic-key", "a non-default-port cursor is rejected")
    })
    {
        var handler = new FakeHttpHandler(_ => JsonResponse(ActivePageJson(cursor, 1, SyntheticBundle)));
        var governor = CreateGovernor();
        var client = CreateClient(handler, governor, out var http);
        await CheckThrows<System.Text.Json.JsonException>(
            () => client.GetActiveBundlesAsync(null, CancellationToken.None),
            name,
            exception => !exception.Message.Contains("synthetic-key", StringComparison.Ordinal));
        http.Dispose();
        governor.Dispose();
    }
}

// A non-success status is a transport error.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("{}", HttpStatusCode.InternalServerError));
    var governor = CreateGovernor();
    var client = CreateClient(handler, governor, out var http);
    await CheckThrows<HttpRequestException>(
        () => client.GetActiveBundlesAsync(null, CancellationToken.None),
        "a non-success status fails the page");
    http.Dispose();
    governor.Dispose();
}

// A 429 is retried once.
{
    var attempts = 0;
    var handler = new FakeHttpHandler(_ =>
    {
        attempts++;
        return attempts == 1
            ? JsonResponse("{}", HttpStatusCode.TooManyRequests)
            : JsonResponse(ActivePageJson("", 0));
    });
    var governor = CreateGovernor();
    var client = CreateClient(handler, governor, out var http);
    var page = await client.GetActiveBundlesAsync(null, CancellationToken.None);
    Check(page.Bundles.Count == 0 && attempts == 2, "a 429 is retried exactly once");
    http.Dispose();
    governor.Dispose();
}

// One active page consumes exactly one governor record: with a one-record budget the second call must
// wait (and here cancels) instead of being admitted.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(ActivePageJson("", 0)));
    var governor = CreateGovernor(perMinute: 1);
    var client = CreateClient(handler, governor, out var http);
    await client.GetActiveBundlesAsync(null, CancellationToken.None);
    using var shortLived = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    await CheckThrows<OperationCanceledException>(
        () => client.GetActiveBundlesAsync(null, shortLived.Token),
        "one active page consumes exactly one record of the shared governor budget");
    http.Dispose();
    governor.Dispose();
}

// --- B. Pure sweep completion -------------------------------------------------

static GgDealsBundle SweepBundle(string key) =>
    new(key, key, null, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null, []);

static GgDealsBundlePage SweepPage(int total, string? next, params string[] keys) =>
    new(keys.Select(SweepBundle).ToList(), total, next);

var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

{
    var sweep = new GgDealsBundleSweep(10, TimeSpan.FromHours(1));
    Check(sweep.NeedsAnotherPage, "a fresh sweep needs its first page");
    Check(sweep.AddPage(SweepPage(2, "c1", "a"), t0), "a page with next asks for another one");
    Check(!sweep.AddPage(SweepPage(2, null, "b"), t0.AddMinutes(1)) && sweep.IsComplete,
        "next null with a matching total completes the catalog");
    Check(sweep.Bundles.Count == 2 && sweep.PageCount == 2, "the complete sweep keeps both pages");
}

{
    var sweep = new GgDealsBundleSweep(10, TimeSpan.FromHours(1));
    Check(sweep.AddPage(SweepPage(5, "c1", "a"), t0), "the first total is recorded");
    Check(!sweep.AddPage(SweepPage(6, "c2", "b"), t0) &&
          sweep.IncompleteReason == GgDealsBundleSweepIncompleteReason.TotalMismatch,
        "a total that changes between pages is incomplete");
}

{
    var sweep = new GgDealsBundleSweep(10, TimeSpan.FromHours(1));
    Check(sweep.AddPage(SweepPage(5, "c1", "a"), t0), "the first page is accepted");
    Check(!sweep.AddPage(SweepPage(5, null, "b"), t0) &&
          sweep.IncompleteReason == GgDealsBundleSweepIncompleteReason.TotalMismatch,
        "a final count that does not match the provider total is incomplete");
}

{
    var sweep = new GgDealsBundleSweep(10, TimeSpan.FromHours(1));
    Check(sweep.AddPage(SweepPage(3, "c1", "a"), t0), "the first cursor is accepted");
    Check(!sweep.AddPage(SweepPage(3, "c1", "b"), t0) &&
          sweep.IncompleteReason == GgDealsBundleSweepIncompleteReason.RepeatedCursor,
        "a repeated next cursor stops the loop");
}

{
    var sweep = new GgDealsBundleSweep(10, TimeSpan.FromHours(1));
    Check(sweep.AddPage(SweepPage(3, "c1", "a"), t0), "the first page is accepted");
    Check(!sweep.AddPage(SweepPage(3, "c2", "a"), t0) &&
          sweep.IncompleteReason == GgDealsBundleSweepIncompleteReason.RepeatedPage,
        "a page whose key set repeats is incomplete");
}

{
    var sweep = new GgDealsBundleSweep(2, TimeSpan.FromHours(1));
    Check(sweep.AddPage(SweepPage(5, "c1", "a"), t0), "the first page of the budget is accepted");
    Check(!sweep.AddPage(SweepPage(5, "c2", "b"), t0) &&
          sweep.IncompleteReason == GgDealsBundleSweepIncompleteReason.PageBudgetExhausted,
        "the page budget bounds a still-open catalog");
}

{
    var sweep = new GgDealsBundleSweep(10, TimeSpan.FromMinutes(60));
    Check(sweep.AddPage(SweepPage(5, "c1", "a"), t0), "the first page opens the cursor window");
    Check(!sweep.AddPage(SweepPage(5, "c2", "b"), t0.AddMinutes(61)) &&
          sweep.IncompleteReason == GgDealsBundleSweepIncompleteReason.CursorExpired,
        "the one-hour cursor window stops the sweep instead of restarting it");
}

{
    var sweep = new GgDealsBundleSweep(10, TimeSpan.FromHours(1));
    Check(sweep.AddPage(SweepPage(5, "c1", "a"), t0), "a page is accepted before the failure");
    sweep.MarkFailed();
    Check(!sweep.IsComplete && sweep.IncompleteReason == GgDealsBundleSweepIncompleteReason.PageFailure,
        "a page failure is incomplete and never certified");
}

// --- C. Service applies only a complete sweep ---------------------------------

static GgDealsBundleSyncService CreateService(
    ScriptedGgDealsClient client, RecordingWriter writer, int maxPages = 10, int cursorMinutes = 60) =>
    new(client, writer, new GgDealsBundleSyncSettings(maxPages, cursorMinutes), NullLogger<GgDealsBundleSyncService>.Instance);

{
    var client = new ScriptedGgDealsClient(cursor => cursor switch
    {
        null => SweepPage(2, "c1", "a"),
        "c1" => SweepPage(2, null, "b"),
        _ => SweepPage(2, null)
    });
    var writer = new RecordingWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(result.Completed && result.Bundles == 2 && result.Written == 2,
        "a complete catalog is applied once");
    Check(writer.Calls == 1 && writer.Last is { Count: 2 },
        "a complete sweep replaces the snapshot exactly once");
}

{
    var client = new ScriptedGgDealsClient(cursor => cursor is null
        ? SweepPage(2, "c1", "a")
        : throw new HttpRequestException("synthetic"));
    var writer = new RecordingWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(!result.Completed && result.IncompleteReason == GgDealsBundleSweepIncompleteReason.PageFailure,
        "a provider failure leaves the sweep incomplete");
    Check(writer.Calls == 0, "a failed sweep never writes, so the previous snapshot is preserved");
}

{
    var client = new ScriptedGgDealsClient(cursor => cursor is null
        ? SweepPage(5, "c1", "a")
        : throw new System.Text.Json.JsonException("synthetic"));
    var writer = new RecordingWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(writer.Calls == 0, "a malformed later page never writes");
    Check(!result.Completed, "a malformed later page preserves the previous snapshot");
}

{
    var client = new ScriptedGgDealsClient(_ => SweepPage(0, null));
    var writer = new RecordingWriter();
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await CheckThrows<OperationCanceledException>(
        () => CreateService(client, writer).SyncAsync(cancelled.Token),
        "caller cancellation propagates out of the service");
    Check(writer.Calls == 0, "cancellation never writes");
}

// --- D. Mapper, key heuristic and source isolation ---------------------------

{
    var bundle = new GgDealsBundle(
        "gg:test",
        "Synthetic Bundle",
        "https://gg.deals/bundle/synthetic/",
        t0,
        null,
        [
            new GgDealsBundleTier(1999, "USD", 2,
            [
                new GgDealsBundleItem("Game A", "https://gg.deals/game/a/"),
                new GgDealsBundleItem("Game B", null)
            ])
        ]);

    var mapped = GgDealsBundleMapper.Map(bundle, t0);
    Check(mapped.Source == "ggdeals", "the mapped bundle keeps the ggdeals source");
    Check(mapped.PageUrl == "https://gg.deals/bundle/synthetic/", "the article URL becomes the page URL");
    Check(mapped.DealUrl is null, "gg.deals has no affiliate deal URL to store");
    Check(mapped.Tiers[0].SelectionCount == 2, "the choose-N count reaches the persisted tier");
    Check(mapped.Tiers[0].Games[0].Url == "https://gg.deals/game/a/", "the item URL survives the mapping");
    Check(mapped.Tiers[0].Games[0].ItadGameId is null,
        "a gg.deals item is never given an invented ITAD identity");
    Check(mapped.Tiers[0].Games.All(game => game.Type == "game"),
        "gg.deals items are typed as games, the provider's only documented granularity");
}

// A choose-N larger than the listed set is incomplete and keeps no count.
{
    var bundle = new GgDealsBundle("gg:x", "X", null, t0, null,
        [new GgDealsBundleTier(1000, "USD", 5, [new GgDealsBundleItem("Only", null)])]);
    var mapped = GgDealsBundleMapper.Map(bundle, t0);
    Check(!mapped.Tiers[0].ItemsComplete, "an unsatisfiable choose-N makes the tier incomplete");
    Check(mapped.Tiers[0].SelectionCount is null, "an unsatisfiable choose-N is not persisted");
}

// The exact same provider bundle yields the exact same key; a different title does not.
{
    var tiers = new[] { new GgDealsBundleTier(1999, "USD", null, [new GgDealsBundleItem("A", null)]) };
    var first = GgDealsBundleKey.Compute("Same", t0, tiers);
    var second = GgDealsBundleKey.Compute("Same", t0, tiers);
    var other = GgDealsBundleKey.Compute("Other", t0, tiers);
    Check(first == second, "the heuristic key is deterministic for the same provider fields");
    Check(first != other, "a different title yields a different heuristic key");
    Check(first.StartsWith(GgDealsBundleKey.Prefix, StringComparison.Ordinal),
        "the heuristic key is namespaced away from ITAD keys");
}

// ITAD defaults are preserved through the existing mapper.
{
    var itadTier = new ItadBundleTier(1000, "USD", false, true,
        [new ItadBundleItem("itad-a", "A", "game", 500, "USD")]);
    var itadMapped = BundleTierMapper.Map([itadTier], 20, 50);
    Check(itadMapped[0].SelectionCount is null, "an ITAD tier keeps a null selection count");
    Check(itadMapped[0].Games[0].Url is null, "an ITAD item keeps a null URL");
    Check(itadMapped[0].Games[0].ItadGameId == "itad-a", "the ITAD UUID is still preserved");
}

Check(EfGgDealsBundleSnapshotWriter.Source == "ggdeals",
    "the gg.deals snapshot uses its own source, isolated from every other provider's purge");
Check(EfGgDealsBundleSnapshotWriter.Source != "itad" && EfGgDealsBundleSnapshotWriter.Source != "itad_global",
    "the gg.deals source differs from both ITAD sources");

// Session advisory lock keys currently in use: wishlist sync 7_831_442_091, ntfy deals 7_831_442_092
// and the global ITAD bundle sweep 7_831_442_093. Sharing a key would make two independent background
// jobs silently skip each other, so the collision is a regression.
Check(GgDealsBundleSyncService.AdvisoryLockKey != 7_831_442_091L &&
      GgDealsBundleSyncService.AdvisoryLockKey != 7_831_442_092L &&
      GgDealsBundleSyncService.AdvisoryLockKey != 7_831_442_093L,
    "the gg.deals sweep lock is distinct from the wishlist, ntfy and global ITAD locks");
Check(GgDealsBundleSyncService.AdvisoryLockKey == 7_831_442_094L,
    "the gg.deals sweep keeps its own documented lock key");

// --- Report -----------------------------------------------------------------

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} gg.deals bundle checks failed");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"  - {failure}");
    }

    Environment.Exit(1);
}

Console.WriteLine("PASS: gg.deals active bundle parser, sweep and mapper checks");

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}

internal sealed class ScriptedGgDealsClient(Func<string?, GgDealsBundlePage> page) : IGgDealsClient
{
    public Task<IReadOnlyDictionary<int, GgDealsGamePrice>> GetPricesAsync(
        IReadOnlyCollection<int> steamAppIds, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<GgDealsBundlePage> GetActiveBundlesAsync(string? cursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(page(cursor));
    }
}

internal sealed class RecordingWriter : IGgDealsBundleSnapshotWriter
{
    public int Calls { get; private set; }
    public IReadOnlyList<GgDealsBundle>? Last { get; private set; }

    public Task<GgDealsBundleWriteResult> ReplaceAsync(
        IReadOnlyList<GgDealsBundle> bundles,
        DateTime observedAt,
        CancellationToken cancellationToken)
    {
        Calls++;
        Last = bundles;
        return Task.FromResult(new GgDealsBundleWriteResult(bundles.Count, 0));
    }
}
