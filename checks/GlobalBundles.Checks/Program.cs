using System.Net;
using System.Text;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Itad;
using Deals.BusinessLogic.Services;
using Microsoft.Extensions.Logging.Abstractions;

// Deterministic, no-database checks for the global ITAD bundle sweep (S2-S3, task T2).
//
// Two layers are exercised here:
//   1. The public ItadClient global page parser against a fake HTTP handler: root array, sanitized
//      tiers/UUIDs, empty catalog, 429 retry, transport failure, cancellation and malformed
//      envelope/URL. The existing per-game overview parser is re-checked to prove it was preserved.
//   2. The pure sweep/snapshot decision: completion at an exact page boundary, budget exhaustion,
//      repeated-page guard and page failure, plus the service apply-only-on-complete rule.
//
// Every fixture here is synthetic and marked as such: it is a schema-shaped payload, not a recorded
// provider response. No real provider is contacted and no credential is used.

var failures = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        failures.Add(name);
    }
}

async Task CheckThrows<TException>(Func<Task> action, string name)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    catch (Exception exception)
    {
        failures.Add($"{name} (threw {exception.GetType().Name} instead of {typeof(TException).Name})");
        return;
    }

    failures.Add($"{name} (no exception)");
}

static ItadBundle SweepBundle(string key) =>
    new(
        key,
        key,
        null,
        null,
        null,
        null,
        null,
        null,
        $"https://isthereanydeal.com/bundle/{key}",
        [],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        true);

static IReadOnlyList<ItadBundle> Page(int count, string prefix) =>
    Enumerable.Range(0, count).Select(index => SweepBundle($"{prefix}-{index}")).ToList();

// --- A. Pure sweep completion: no HTTP, no database ------------------------

var twoPages = new GlobalBundleSweep(50, 40);
Check(twoPages.NeedsAnotherPage, "a fresh sweep needs its first page");
Check(twoPages.AddPage(Page(50, "a")), "a full page asks for the next one");
Check(twoPages.AddPage(Page(50, "b")), "a second full page still asks for the next one");
Check(!twoPages.AddPage(Page(0, "c")), "an empty page ends the catalog");
Check(twoPages.IsComplete && twoPages.IncompleteReason == GlobalBundleSweepIncompleteReason.None,
    "two full pages plus an empty one is a complete snapshot");
Check(twoPages.PageCount == 3 && twoPages.Bundles.Count == 100,
    "the complete sweep keeps both pages and their page count");

var exact = new GlobalBundleSweep(50, 40);
Check(exact.AddPage(Page(50, "a")), "exactly 50 items are not the end of the catalog");
Check(!exact.IsComplete, "an exact-50 page alone is never certified complete");
Check(!exact.AddPage(Page(0, "b")) && exact.IsComplete,
    "the extra page completes the exact-50 boundary");

var empty = new GlobalBundleSweep(50, 40);
Check(!empty.AddPage(Page(0, "a")) && empty.IsComplete, "an empty catalog is a complete snapshot");
Check(empty.Bundles.Count == 0, "an authoritative empty snapshot keeps zero bundles");

var repeated = new GlobalBundleSweep(50, 40);
Check(repeated.AddPage(Page(50, "a")), "the first page is accepted");
Check(!repeated.AddPage(Page(50, "a")), "a repeated page stops the sweep");
Check(!repeated.IsComplete &&
      repeated.IncompleteReason == GlobalBundleSweepIncompleteReason.RepeatedPage,
    "a repeated page is incomplete and never certified");

var budget = new GlobalBundleSweep(50, 2);
Check(budget.AddPage(Page(50, "a")), "the first page of the budget is accepted");
Check(!budget.AddPage(Page(50, "b")), "the page budget stops a still-full catalog");
Check(!budget.IsComplete &&
      budget.IncompleteReason == GlobalBundleSweepIncompleteReason.PageBudgetExhausted,
    "budget exhaustion preserves instead of certifying");

var failed = new GlobalBundleSweep(50, 40);
Check(failed.AddPage(Page(50, "a")), "a page is accepted before the failure");
failed.MarkFailed();
Check(!failed.IsComplete && failed.IncompleteReason == GlobalBundleSweepIncompleteReason.PageFailure,
    "a page failure is incomplete and never certified");

// --- B. Public ItadClient global parser against a fake HTTP handler --------

// Synthetic bundle shaped like the documented `bundles[]`/`bundles/v1` entry. Not a recorded
// response: the ids and titles are invented for the check.
const string SyntheticBundleJson = """
{
  "id": "11111111-1111-1111-1111-111111111111",
  "title": "Synthetic Bundle",
  "page": { "id": "fanatical", "name": "Fanatical", "shopId": 61 },
  "url": "https://isthereanydeal.com/bundle/synthetic?ref=synthetic",
  "details": "https://isthereanydeal.com/bundle/synthetic",
  "isMature": false,
  "publish": "2026-10-01T00:00:00Z",
  "expiry": null,
  "note": null,
  "counts": { "games": 2, "media": 0 },
  "tiers": [
    {
      "price": { "amountInt": 1999, "currency": "USD" },
      "addon": false,
      "games": [
        { "id": "22222222-2222-2222-2222-222222222222", "slug": "a", "title": "Game A", "type": "game", "mature": false },
        { "id": "33333333-3333-3333-3333-333333333333", "slug": "b", "title": "Game B", "type": "package", "mature": false }
      ]
    }
  ]
}
""";

const string MissingUrlBundleJson = """
{
  "id": "44444444-4444-4444-4444-444444444444",
  "title": "Synthetic Bundle Without Url",
  "page": { "id": "fanatical", "name": "Fanatical", "shopId": 61 },
  "details": null,
  "publish": "2026-10-01T00:00:00Z",
  "expiry": null,
  "counts": { "games": 1, "media": 0 },
  "tiers": [
    {
      "price": { "amountInt": 999, "currency": "USD" },
      "addon": false,
      "games": [
        { "id": "22222222-2222-2222-2222-222222222222", "slug": "a", "title": "Game A", "type": "game", "mature": false }
      ]
    }
  ]
}
""";

static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
{
    var response = new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    return response;
}

// Synthetic page generator: `count` minimal bundle objects with distinct ids. Not a recorded response.
static string SyntheticPage(int count, string prefix) =>
    "[" + string.Join(",", Enumerable.Range(0, count).Select(index =>
        $$"""
        { "id": "{{prefix}}-{{index}}", "title": "{{prefix}}-{{index}}", "url": "https://isthereanydeal.com/bundle/{{prefix}}-{{index}}", "tiers": [] }
        """)) + "]";

static ItadClient CreateClient(FakeHttpHandler handler, out HttpClient http, out ItadRequestGovernor governor)
{
    http = new HttpClient(handler) { BaseAddress = new Uri("https://api.isthereanydeal.com/") };
    governor = new ItadRequestGovernor(1000, 100, 0, NullLogger<ItadRequestGovernor>.Instance);
    var settings = new ItadClientSettings(
        "synthetic-key",
        "MX",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "61" });
    return new ItadClient(http, settings, governor);
}

// One valid global page.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("[" + SyntheticBundleJson + "]"));
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetGlobalBundlesPageAsync(0, 50, CancellationToken.None);
    Check(page.Count == 1, "the global root array parses one bundle");
    Check(page[0].BundleKey == "11111111-1111-1111-1111-111111111111", "the bundle id is the key");
    Check(page[0].Title == "Synthetic Bundle", "the title survives sanitization");
    Check(page[0].Url == "https://isthereanydeal.com/bundle/synthetic?ref=synthetic",
        "the affiliate url is preserved verbatim");
    Check(page[0].ShopId == "fanatical" && page[0].ShopName == "Fanatical",
        "the page object fills the shop metadata");
    Check(page[0].Tiers.Count == 1 && page[0].Tiers[0].PriceMinor == 1999 &&
          page[0].Tiers[0].Currency == "USD", "the tier price and currency are sanitized");
    Check(page[0].AttributionComplete, "a fully scanned global bundle is attributable");
    Check(page[0].MatchedItadIds.SetEquals(new[]
        {
            "22222222-2222-2222-2222-222222222222",
            "33333333-3333-3333-3333-333333333333"
        }),
        "every observed item id is reported, not only a queried id");
    Check(handler.Requests.Count == 1 && handler.Requests[0].Query.Contains("offset=0&limit=50"),
        "the page request carries the offset and limit");
    Check(handler.Requests[0].Query.Contains("country=MX") &&
          handler.Requests[0].Query.Contains("mature=true") &&
          handler.Requests[0].Query.Contains("expired=false"),
        "the page request carries country/mature/expired");
    Check(handler.ApiKeyHeaders.All(value => value == "synthetic-key") && handler.ApiKeyHeaders.Count == 1,
        "the API key travels only in the ITAD header");
    http.Dispose();
    governor.Dispose();
}

// A later page honors its offset.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("[" + SyntheticBundleJson + "]"));
    var client = CreateClient(handler, out var http, out var governor);
    await client.GetGlobalBundlesPageAsync(50, 50, CancellationToken.None);
    Check(handler.Requests[0].Query.Contains("offset=50&limit=50"), "the second page requests offset 50");
    http.Dispose();
    governor.Dispose();
}

// The public parser plus the sweep complete an exact-50 boundary over two pages.
{
    var handler = new FakeHttpHandler(request =>
        request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
            ? JsonResponse(SyntheticPage(50, "p"))
            : JsonResponse("[]"));
    var client = CreateClient(handler, out var http, out var governor);
    var sweep = new GlobalBundleSweep(50, 40);
    var offset = 0;
    while (sweep.NeedsAnotherPage)
    {
        var page = await client.GetGlobalBundlesPageAsync(offset, 50, CancellationToken.None);
        if (!sweep.AddPage(page))
        {
            break;
        }

        offset += 50;
    }

    Check(sweep.IsComplete && sweep.Bundles.Count == 50 && sweep.PageCount == 2,
        "the public parser plus the sweep complete an exact-50 boundary over two pages");
    http.Dispose();
    governor.Dispose();
}

// A provider that ignores offset is caught through the public parser too.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(SyntheticPage(50, "p")));
    var client = CreateClient(handler, out var http, out var governor);
    var sweep = new GlobalBundleSweep(50, 40);
    var offset = 0;
    while (sweep.NeedsAnotherPage)
    {
        var page = await client.GetGlobalBundlesPageAsync(offset, 50, CancellationToken.None);
        if (!sweep.AddPage(page))
        {
            break;
        }

        offset += 50;
    }

    Check(!sweep.IsComplete && sweep.IncompleteReason == GlobalBundleSweepIncompleteReason.RepeatedPage,
        "a provider that always returns the same page is caught over fake HTTP");
    http.Dispose();
    governor.Dispose();
}

// Empty catalog.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("[]"));
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetGlobalBundlesPageAsync(0, 50, CancellationToken.None);
    Check(page.Count == 0, "an empty root array is an authoritative empty page");
    http.Dispose();
    governor.Dispose();
}

// Unknown envelope.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("{\"bundles\":[]}"));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetGlobalBundlesPageAsync(0, 50, CancellationToken.None),
        "a non-array root envelope is rejected loudly");
    http.Dispose();
    governor.Dispose();
}

// Structural missing URL.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("[" + MissingUrlBundleJson + "]"));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetGlobalBundlesPageAsync(0, 50, CancellationToken.None),
        "a bundle without its mandatory url fails the page instead of being dropped");
    http.Dispose();
    governor.Dispose();
}

// Non-object page entry.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("[42]"));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetGlobalBundlesPageAsync(0, 50, CancellationToken.None),
        "a non-object bundle entry is rejected loudly");
    http.Dispose();
    governor.Dispose();
}

// Transport failure.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("{}", HttpStatusCode.InternalServerError));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<HttpRequestException>(
        () => client.GetGlobalBundlesPageAsync(0, 50, CancellationToken.None),
        "a non-success status fails the page");
    http.Dispose();
    governor.Dispose();
}

// 429 retry with Retry-After.
{
    var attempts = 0;
    var handler = new FakeHttpHandler(_ =>
    {
        attempts++;
        if (attempts == 1)
        {
            var tooMany = JsonResponse("{}", HttpStatusCode.TooManyRequests);
            tooMany.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return tooMany;
        }

        return JsonResponse("[" + SyntheticBundleJson + "]");
    });
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetGlobalBundlesPageAsync(0, 50, CancellationToken.None);
    Check(page.Count == 1 && attempts == 2, "a 429 is retried once after Retry-After");
    http.Dispose();
    governor.Dispose();
}

// Cancellation.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("[]"));
    var client = CreateClient(handler, out var http, out var governor);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await CheckThrows<OperationCanceledException>(
        () => client.GetGlobalBundlesPageAsync(0, 50, cancelled.Token),
        "a cancelled page request propagates cancellation");
    http.Dispose();
    governor.Dispose();
}

// Preserve the per-game overview parser: a missing-url bundle is still silently dropped and
// membership is still attributed only through the queried id.
{
    const string queried = "22222222-2222-2222-2222-222222222222";
    var overview = "{\"prices\":[],\"bundles\":[" + SyntheticBundleJson + "," + MissingUrlBundleJson + "]}";
    var handler = new FakeHttpHandler(request =>
        request.RequestUri!.AbsolutePath.Contains("overview", StringComparison.OrdinalIgnoreCase)
            ? JsonResponse(overview)
            : JsonResponse("[]"));
    var client = CreateClient(handler, out var http, out var governor);
    var bundles = await client.GetBundlesAsync([queried], CancellationToken.None);
    Check(bundles.Count == 1, "the per-game overview still drops the url-less bundle");
    Check(bundles[0].AttributionComplete &&
          bundles[0].MatchedItadIds.SetEquals(new[] { queried }),
        "the per-game overview still attributes by the queried id only");
    http.Dispose();
    governor.Dispose();
}

// Preserve the per-game authoritative empty answer.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("{\"prices\":[],\"bundles\":[]}"));
    var client = CreateClient(handler, out var http, out var governor);
    var bundles = await client.GetBundlesAsync(
        ["22222222-2222-2222-2222-222222222222"], CancellationToken.None);
    Check(bundles.Count == 0, "the per-game overview still treats an empty bundles array as authoritative");
    http.Dispose();
    governor.Dispose();
}

// --- C. Service applies only a complete sweep ------------------------------

static GlobalBundleSyncService CreateService(
    ScriptedItadClient client, RecordingWriter writer, int pageSize = 50, int maxPages = 40) =>
    new(client, writer, new GlobalBundleSyncSettings(pageSize, maxPages), NullLogger<GlobalBundleSyncService>.Instance);

{
    var client = new ScriptedItadClient(offset => offset switch
    {
        0 => Page(50, "a"),
        50 => Page(50, "b"),
        _ => Page(0, "c")
    });
    var writer = new RecordingWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(result.Completed, "two full pages plus an empty one completes through the service");
    Check(result.Pages == 3 && result.Bundles == 100 && result.Written == 100,
        "the completed service result counts pages, bundles and writes");
    Check(writer.Calls == 1 && writer.Last is { Count: 100 },
        "a complete sweep replaces the snapshot exactly once");
}

{
    var client = new ScriptedItadClient(_ => Page(50, "a")) { Failure = new HttpRequestException("synthetic") };
    var writer = new RecordingWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(!result.Completed && result.IncompleteReason == GlobalBundleSweepIncompleteReason.PageFailure,
        "a provider failure leaves the sweep incomplete");
    Check(writer.Calls == 0, "a failed sweep never writes, so the previous snapshot is preserved");
}

{
    var client = new ScriptedItadClient(_ => Page(50, "a"));
    var writer = new RecordingWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(!result.Completed && result.IncompleteReason == GlobalBundleSweepIncompleteReason.RepeatedPage,
        "a provider that ignores offset is detected as a repeated page");
    Check(writer.Calls == 0, "a repeated page never writes, so the previous snapshot is preserved");
}

{
    var client = new ScriptedItadClient(offset => offset == 0 ? Page(50, "a") : Page(50, "b"));
    var writer = new RecordingWriter();
    var result = await CreateService(client, writer, maxPages: 2).SyncAsync(CancellationToken.None);
    Check(!result.Completed && result.IncompleteReason == GlobalBundleSweepIncompleteReason.PageBudgetExhausted,
        "the page budget bounds the sweep");
    Check(writer.Calls == 0, "budget exhaustion never writes, so the previous snapshot is preserved");
}

{
    var client = new ScriptedItadClient(_ => Page(0, "a"));
    var writer = new RecordingWriter();
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await CheckThrows<OperationCanceledException>(
        () => CreateService(client, writer).SyncAsync(cancelled.Token),
        "caller cancellation propagates out of the service");
    Check(writer.Calls == 0, "cancellation never writes");
}

// --- D. Source isolation ----------------------------------------------------

Check(EfGlobalBundleSnapshotWriter.Source == "itad_global",
    "the global snapshot uses its own source, isolated from the per-game itad orphan purge");
Check(EfGlobalBundleSnapshotWriter.Source != "itad",
    "the global source differs from the source the existing orphan purge scopes to");

// Session advisory lock keys currently in use: wishlist sync 7_831_442_091 and ntfy deals
// 7_831_442_092 (the per-user category locks live in the disjoint 0x574C00000000 range). Sharing a key
// would make two independent background jobs silently skip each other, so the collision is a regression.
Check(GlobalBundleSyncService.AdvisoryLockKey != 7_831_442_091L,
    "the global sweep lock is distinct from the wishlist sync lock");
Check(GlobalBundleSyncService.AdvisoryLockKey != 7_831_442_092L,
    "the global sweep lock is distinct from the ntfy deals lock");

// --- Report -----------------------------------------------------------------

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} global bundle checks failed");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"  - {failure}");
    }

    Environment.Exit(1);
}

Console.WriteLine("PASS: global bundle sweep and parser checks");

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    public List<string> ApiKeyHeaders { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);
        if (request.Headers.TryGetValues("ITAD-API-Key", out var values))
        {
            ApiKeyHeaders.AddRange(values);
        }

        return Task.FromResult(respond(request));
    }
}

internal sealed class ScriptedItadClient(Func<int, IReadOnlyList<ItadBundle>> page) : IItadClient
{
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<ItadBundle>> GetGlobalBundlesPageAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(page(offset));
    }

    public Task<string?> LookupSteamAppIdAsync(
        int appId, CancellationToken cancellationToken, ItadRequestPriority priority = ItadRequestPriority.Background) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ItadGamePrices>> GetPricesAsync(
        IReadOnlyCollection<string> itadIds, CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ItadBundle>> GetBundlesAsync(
        IReadOnlyCollection<string> itadIds, CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background) =>
        throw new NotSupportedException();

    public Task<string?> ResolveDealUrlAsync(
        string dealUrl, CancellationToken cancellationToken,
        ItadRequestPriority priority = ItadRequestPriority.Background) =>
        throw new NotSupportedException();
}

internal sealed class RecordingWriter : IGlobalBundleSnapshotWriter
{
    public int Calls { get; private set; }
    public IReadOnlyList<ItadBundle>? Last { get; private set; }

    public Task<GlobalBundleWriteResult> ReplaceAsync(
        IReadOnlyList<ItadBundle> bundles,
        DateTime observedAt,
        CancellationToken cancellationToken)
    {
        Calls++;
        Last = bundles;
        return Task.FromResult(new GlobalBundleWriteResult(bundles.Count, 0));
    }
}
