using System.Net;
using System.Text;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Services;
using Microsoft.Extensions.Logging.Abstractions;

// Deterministic, no-database checks for the global ITAD deals feed (spec S1/S4, task T7).
//
// Fixtures are shaped after the PRIMARY published contract, not invented: IsThereAnyDeal API 2.11.0
// OpenAPI (https://docs.isthereanydeal.com/openapi.yaml), GET /deals/v2 -> resp.deals and its published
// example resp.deals.get.v2.success. The list item carries the game fields (id/slug/title/type/mature)
// and a nested `deal` object (shop/price/regular/cut/historyLow/expiry/url). No real provider call and
// no credential are used.
//
// Layers exercised here:
//   1. The public ItadGlobalDealClient parser over a fake HTTP handler: page envelope, key header,
//      query bounds, hasMore/nextOffset, UTC expiry, malformed currency/price, 429 retry, transport
//      failure and cancellation. Credential-free error messages are asserted explicitly.
//   2. The primary shape is required: the old invented top-level shape is rejected.
//   3. The pure GlobalDealSweep completion rule: hasMore end, repeated page, cursor loop, page budget
//      and page failure.
//   4. GlobalDealSyncService apply-only-on-complete through a scripted client and a recording writer.
//   5. Pure policy/currency/wishlist rules: no fabricated discount or score, foreign unconverted not
//      price-eligible, wishlist exclusion by exact ITAD id.
//   6. Source and advisory-lock isolation.

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

// --- Pure sweep -------------------------------------------------------------

static GlobalDealPage Page(int count, string prefix, bool hasMore = false, int? nextOffset = null) =>
    new(
        Enumerable.Range(0, count).Select(index => Deal($"{prefix}-{index}")).ToList(),
        hasMore,
        nextOffset);

static ItadGlobalDeal Deal(
    string key,
    bool official = true,
    string currency = "MXN",
    int price = 1000,
    string gameId = "11111111-1111-1111-1111-111111111111") =>
    new(
        key,
        gameId,
        key,
        null,
        "game",
        false,
        "61",
        "Steam",
        official,
        currency,
        price,
        2000,
        50,
        null,
        null,
        "https://isthereanydeal.com/deal/x?ref=aff",
        null);

{
    var sweep = new GlobalDealSweep(2, 10);
    Check(sweep.NeedsAnotherPage, "a fresh deals sweep needs its first page");
    Check(sweep.IncompleteReason == GlobalDealSweepIncompleteReason.None && !sweep.IsComplete,
        "a fresh sweep is neither complete nor failed");
}

{
    var sweep = new GlobalDealSweep(2, 10);
    Check(!sweep.AddPage(Page(2, "a", hasMore: false), 0), "hasMore=false ends the catalog");
    Check(sweep.IsComplete && sweep.IncompleteReason == GlobalDealSweepIncompleteReason.None,
        "a page without more is a complete snapshot");
    Check(sweep.PageCount == 1 && sweep.Deals.Count == 2,
        "the complete sweep keeps the page and its deals");
}

{
    var sweep = new GlobalDealSweep(2, 10);
    Check(sweep.AddPage(Page(2, "a", hasMore: true, nextOffset: 2), 0), "a full page asks for the next one");
    Check(!sweep.AddPage(Page(1, "b", hasMore: false), 2), "the short final page ends the catalog");
    Check(sweep.IsComplete && sweep.PageCount == 2 && sweep.Deals.Count == 3,
        "two pages with an explicit end are a complete snapshot");
}

{
    var sweep = new GlobalDealSweep(2, 10);
    Check(sweep.AddPage(Page(2, "a", hasMore: true, nextOffset: 2), 0), "the first page is accepted");
    Check(!sweep.AddPage(Page(2, "a", hasMore: true, nextOffset: 2), 2), "a repeated page stops the sweep");
    Check(!sweep.IsComplete &&
          sweep.IncompleteReason == GlobalDealSweepIncompleteReason.RepeatedPage,
        "a repeated page is incomplete and never certified");
}

{
    var sweep = new GlobalDealSweep(2, 10);
    Check(sweep.AddPage(Page(2, "a", hasMore: true, nextOffset: 2), 0), "the first page is accepted");
    Check(!sweep.AddPage(Page(2, "b", hasMore: true, nextOffset: 2), 2),
        "a cursor that does not advance stops the sweep");
    Check(!sweep.IsComplete &&
          sweep.IncompleteReason == GlobalDealSweepIncompleteReason.CursorLoop,
        "a stalled cursor is incomplete and never certified");
}

{
    var sweep = new GlobalDealSweep(2, 2);
    Check(sweep.AddPage(Page(2, "a", hasMore: true, nextOffset: 2), 0), "the first page of the budget is accepted");
    Check(!sweep.AddPage(Page(2, "b", hasMore: true, nextOffset: 4), 2),
        "the page budget stops a provider that still reports more");
    Check(!sweep.IsComplete &&
          sweep.IncompleteReason == GlobalDealSweepIncompleteReason.PageBudgetExhausted,
        "budget exhaustion preserves instead of certifying");
}

{
    var sweep = new GlobalDealSweep(2, 10);
    Check(sweep.AddPage(Page(2, "a", hasMore: true, nextOffset: 2), 0), "a page is accepted before the failure");
    sweep.MarkFailed();
    Check(!sweep.IsComplete && sweep.IncompleteReason == GlobalDealSweepIncompleteReason.PageFailure,
        "a page failure is incomplete and never certified");
}

await CheckThrows<ArgumentOutOfRangeException>(
    () => Task.FromResult(new GlobalDealSweep(201, 10)),
    "a limit above the provider cap is rejected by the pure sweep");
await CheckThrows<ArgumentOutOfRangeException>(
    () => Task.FromResult(new GlobalDealSweep(0, 10)),
    "a non-positive limit is rejected by the pure sweep");

// --- Public parser over fake HTTP (primary schema shape) --------------------

// Shape copied from IsThereAnyDeal API 2.11.0 OpenAPI resp.deals.list[].game fields + nested deal,
// using the published Shift Quantum example values. It is a schema-shaped sample, not a live capture.
const string SyntheticDealJson = """
{
  "id": "018d937e-e9ec-7103-97a0-732d4301eb72",
  "slug": "shift-quantum",
  "title": "Shift Quantum",
  "type": "game",
  "mature": false,
  "assets": { "boxart": "https://assets.isthereanydeal.com/018d937e-e9ec-7103-97a0-732d4301eb72/boxart.jpg" },
  "deal": {
    "shop": { "id": 50, "name": "Nuuvem" },
    "price": { "amount": 11.4, "amountInt": 1140, "currency": "BRL" },
    "regular": { "amount": 37.99, "amountInt": 3799, "currency": "BRL" },
    "cut": 70,
    "voucher": null,
    "storeLow": { "amount": 9.49, "amountInt": 949, "currency": "BRL" },
    "historyLow": { "amount": 7.59, "amountInt": 759, "currency": "BRL" },
    "historyLow_1y": { "amount": 9.49, "amountInt": 949, "currency": "BRL" },
    "historyLow_3m": { "amount": 9.49, "amountInt": 949, "currency": "BRL" },
    "flag": null,
    "drm": [ { "id": 61, "name": "Steam" } ],
    "platforms": [ { "id": 1, "name": "Windows" } ],
    "timestamp": "2024-12-05T04:26:01+01:00",
    "expiry": "2999-12-19T03:59:59+01:00",
    "url": "https://itad.link/018d9386-c580-739b-bac6-06f2a7eb5d61/"
  }
}
""";

// The invented top-level shape used before the primary contract was inspected. It must be rejected.
const string LegacyInventedShape = """
{
  "id": "deal-1",
  "game": { "id": "11111111-1111-1111-1111-111111111111", "title": "Game A", "type": "game" },
  "shop": { "id": 50, "name": "Nuuvem" },
  "price": { "amountInt": 1140, "currency": "BRL" },
  "url": "https://isthereanydeal.com/deal/deal-1?ref=aff"
}
""";

static string Envelope(string list, bool hasMore, int? nextOffset = null)
{
    var suffix = hasMore
        ? $", \"hasMore\": true, \"nextOffset\": {nextOffset ?? 1}"
        : ", \"hasMore\": false";
    return $"{{\"list\": [{list}]{suffix}}}";
}

static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
{
    var response = new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    return response;
}

static ItadGlobalDealClient CreateClient(
    FakeHttpHandler handler,
    out HttpClient http,
    out ItadRequestGovernor governor,
    string country = "MX",
    string key = "synthetic-key")
{
    http = new HttpClient(handler) { BaseAddress = new Uri("https://api.isthereanydeal.com/") };
    governor = new ItadRequestGovernor(1000, 100, 0, NullLogger<ItadRequestGovernor>.Instance);
    var settings = new ItadClientSettings(
        key,
        country,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "50" });
    return new ItadGlobalDealClient(http, settings, governor);
}

// One valid primary-shape page.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(SyntheticDealJson, hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetDealsPageAsync(0, 200, CancellationToken.None);
    Check(page.Deals.Count == 1 && !page.HasMore, "one valid deal parses and ends the page");
    var deal = page.Deals[0];
    Check(deal.ItadGameId == "018d937e-e9ec-7103-97a0-732d4301eb72",
        "the item-level id is the ITAD game id");
    Check(deal.DealKey == "018d937e-e9ec-7103-97a0-732d4301eb72:50",
        "the deal key is composed from game id and shop id (the contract publishes no deal id)");
    Check(deal.GameTitle == "Shift Quantum" && deal.GameSlug == "shift-quantum" &&
          deal.GameType == "game" && !deal.GameMature,
        "the game title, slug, type and mature flag come from the list item");
    Check(deal.ShopId == "50" && deal.ShopName == "Nuuvem" && deal.IsOfficial,
        "the nested deal shop is read and classified against the allowlist");
    Check(deal.Currency == "BRL" && deal.CurrentPriceMinor == 1140 && deal.RegularPriceMinor == 3799,
        "the nested deal price and regular amounts are preserved");
    Check(deal.DiscountPercent == 70, "the nested deal cut is the discount");
    Check(deal.HistoryLowMinor == 759 && deal.HistoryLowCurrency == "BRL",
        "the nested deal historyLow price object is read directly");
    Check(deal.DealUrl == "https://itad.link/018d9386-c580-739b-bac6-06f2a7eb5d61/",
        "the nested deal url is preserved verbatim");
    Check(handler.ApiKeyHeaders.Count == 1 && handler.ApiKeyHeaders[0] == "synthetic-key",
        "the API key travels only in the ITAD header");
    Check(handler.Requests[0].Query.Contains("offset=0") &&
          handler.Requests[0].Query.Contains("limit=200") &&
          handler.Requests[0].Query.Contains("country=MX") &&
          handler.Requests[0].Query.Contains("shops=50") &&
          handler.Requests[0].Query.Contains("sort=-cut"),
        "the page request carries country, shops, sort, offset and limit");
    http.Dispose();
    governor.Dispose();
}

// The old invented top-level shape is not the contract and must fail loudly.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(LegacyInventedShape, hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetDealsPageAsync(0, 200, CancellationToken.None),
        "the invented top-level shape is rejected: the real item has no top-level price/url");
    http.Dispose();
    governor.Dispose();
}

// HasMore / NextOffset envelope.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(SyntheticDealJson, hasMore: true, nextOffset: 400)));
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetDealsPageAsync(200, 200, CancellationToken.None);
    Check(page.HasMore && page.NextOffset == 400, "the provider nextOffset is carried through");
    Check(handler.Requests[0].Query.Contains("offset=200"), "the requested offset is sent");
    http.Dispose();
    governor.Dispose();
}

// A missing nextOffset falls back to offset + observed items.
{
    const string envelope = "{\"list\": [" + SyntheticDealJson + "], \"hasMore\": true}";
    var handler = new FakeHttpHandler(_ => JsonResponse(envelope));
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetDealsPageAsync(100, 200, CancellationToken.None);
    Check(page.NextOffset == 101, "a missing nextOffset falls back to offset + observed items");
    http.Dispose();
    governor.Dispose();
}

// Limit clamp.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope("", hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    await client.GetDealsPageAsync(0, 5000, CancellationToken.None);
    Check(handler.Requests[0].Query.Contains("limit=200"), "a limit above 200 is clamped before the request");
    http.Dispose();
    governor.Dispose();
}

// Non-official shop is read but not marked official.
{
    var otherShop = SyntheticDealJson.Replace("\"shop\": { \"id\": 50", "\"shop\": { \"id\": 99");
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(otherShop, hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetDealsPageAsync(0, 200, CancellationToken.None);
    Check(page.Deals.Count == 1 && !page.Deals[0].IsOfficial,
        "a shop outside the allowlist is parsed but never marked official");
    http.Dispose();
    governor.Dispose();
}

// Expiry UTC (contract puts expiry inside the nested deal).
{
    var offsetDeal = SyntheticDealJson.Replace(
        "\"expiry\": \"2999-12-19T03:59:59+01:00\"",
        "\"expiry\": \"2999-01-01T06:00:00-06:00\"");
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(offsetDeal, hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetDealsPageAsync(0, 200, CancellationToken.None);
    var expiry = page.Deals[0].ExpiresAt;
    Check(expiry is { Kind: DateTimeKind.Utc } && expiry.Value == new DateTime(2999, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        "a zoned expiry is normalized to the same UTC instant");
    http.Dispose();
    governor.Dispose();
}

// A genuinely expired deal is dropped.
{
    var expired = SyntheticDealJson.Replace(
        "\"expiry\": \"2999-12-19T03:59:59+01:00\"",
        "\"expiry\": \"2000-01-01T00:00:00Z\"");
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(expired, hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetDealsPageAsync(0, 200, CancellationToken.None);
    Check(page.Deals.Count == 0 && !page.HasMore, "an expired deal is dropped instead of shown as active");
    http.Dispose();
    governor.Dispose();
}

// Malformed currency inside the nested price.
{
    var bad = SyntheticDealJson.Replace("\"currency\": \"BRL\"", "\"currency\": \"BR\"");
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(bad, hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetDealsPageAsync(0, 200, CancellationToken.None),
        "a malformed currency fails the page");
    http.Dispose();
    governor.Dispose();
}

// Malformed price (negative) inside the nested price.
{
    var bad = SyntheticDealJson.Replace("\"amountInt\": 1140", "\"amountInt\": -1");
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(bad, hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetDealsPageAsync(0, 200, CancellationToken.None),
        "a negative price fails the page");
    http.Dispose();
    governor.Dispose();
}

// Missing nested price object.
{
    var bad = SyntheticDealJson.Replace(
        "\"price\": { \"amount\": 11.4, \"amountInt\": 1140, \"currency\": \"BRL\" },",
        string.Empty);
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(bad, hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetDealsPageAsync(0, 200, CancellationToken.None),
        "a missing nested price fails the page");
    http.Dispose();
    governor.Dispose();
}

// Unknown envelope shapes.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("{\"list\": {}}"));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetDealsPageAsync(0, 200, CancellationToken.None),
        "a non-array list is rejected loudly");
    http.Dispose();
    governor.Dispose();
}

{
    var handler = new FakeHttpHandler(_ => JsonResponse("{\"list\": []}"));
    var client = CreateClient(handler, out var http, out var governor);
    await CheckThrows<System.Text.Json.JsonException>(
        () => client.GetDealsPageAsync(0, 200, CancellationToken.None),
        "a missing hasMore is rejected instead of being guessed");
    http.Dispose();
    governor.Dispose();
}

// Transport failure, with a credential-free message.
{
    var handler = new FakeHttpHandler(_ => JsonResponse("{}", HttpStatusCode.InternalServerError));
    var client = CreateClient(handler, out var http, out var governor);
    var message = string.Empty;
    try
    {
        await client.GetDealsPageAsync(0, 200, CancellationToken.None);
    }
    catch (HttpRequestException exception)
    {
        message = exception.Message;
    }

    Check(message.Length > 0, "a non-success status fails the page");
    Check(!message.Contains("synthetic-key", StringComparison.Ordinal) &&
          !message.Contains("key=", StringComparison.Ordinal) &&
          !message.Contains("isthereanydeal.com", StringComparison.Ordinal),
        "the transport error message never echoes the key or the request URL");
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

        return JsonResponse(Envelope(SyntheticDealJson, hasMore: false));
    });
    var client = CreateClient(handler, out var http, out var governor);
    var page = await client.GetDealsPageAsync(0, 200, CancellationToken.None);
    Check(page.Deals.Count == 1 && attempts == 2, "a 429 is retried once after Retry-After");
    http.Dispose();
    governor.Dispose();
}

// Cancellation propagates.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope("", hasMore: false)));
    var client = CreateClient(handler, out var http, out var governor);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await CheckThrows<OperationCanceledException>(
        () => client.GetDealsPageAsync(0, 200, cancelled.Token),
        "a cancelled page request propagates cancellation");
    http.Dispose();
    governor.Dispose();
}

// The parser over HTTP catches a provider that ignores the cursor.
{
    var handler = new FakeHttpHandler(_ => JsonResponse(Envelope(SyntheticDealJson, hasMore: true, nextOffset: 0)));
    var client = CreateClient(handler, out var http, out var governor);
    var sweep = new GlobalDealSweep(2, 10);
    var offset = 0;
    while (sweep.NeedsAnotherPage)
    {
        var page = await client.GetDealsPageAsync(offset, 2, CancellationToken.None);
        if (!sweep.AddPage(page, offset))
        {
            break;
        }

        offset = page.NextOffset!.Value;
    }

    Check(!sweep.IsComplete && sweep.IncompleteReason == GlobalDealSweepIncompleteReason.CursorLoop,
        "a provider that never advances the cursor is caught over fake HTTP");
    http.Dispose();
    governor.Dispose();
}

// --- Service applies only a complete sweep ----------------------------------

static GlobalDealSyncService CreateService(
    ScriptedGlobalDealClient client,
    RecordingGlobalDealWriter writer,
    int pageSize = 2,
    int maxPages = 10) =>
    new(client, writer, new GlobalDealSyncSettings(pageSize, maxPages), NullLogger<GlobalDealSyncService>.Instance);

{
    var client = new ScriptedGlobalDealClient(offset => offset switch
    {
        0 => new GlobalDealPage([Deal("a"), Deal("b")], true, 2),
        _ => new GlobalDealPage([Deal("c")], false, null)
    });
    var writer = new RecordingGlobalDealWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(result.Completed && result.Pages == 2 && result.Deals == 3 && result.Written == 3,
        "a complete sweep reports pages, deals and writes");
    Check(writer.Calls == 1 && writer.Last is { Count: 3 },
        "a complete sweep replaces the snapshot exactly once");
}

{
    var client = new ScriptedGlobalDealClient(_ => new GlobalDealPage([Deal("a")], false, null))
    {
        Failure = new HttpRequestException("synthetic")
    };
    var writer = new RecordingGlobalDealWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(!result.Completed && result.IncompleteReason == GlobalDealSweepIncompleteReason.PageFailure,
        "a provider failure leaves the sweep incomplete");
    Check(writer.Calls == 0, "a failed sweep never writes, so the previous snapshot is preserved");
}

{
    var client = new ScriptedGlobalDealClient(_ => new GlobalDealPage([Deal("a"), Deal("b")], true, 0));
    var writer = new RecordingGlobalDealWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(!result.Completed && result.IncompleteReason == GlobalDealSweepIncompleteReason.CursorLoop,
        "a stalled cursor leaves the sweep incomplete");
    Check(writer.Calls == 0, "a stalled cursor never writes, so the previous snapshot is preserved");
}

{
    var client = new ScriptedGlobalDealClient(offset => new GlobalDealPage(
        offset == 0 ? [Deal("a"), Deal("b")] : [Deal("c"), Deal("d")],
        true,
        offset + 2));
    var writer = new RecordingGlobalDealWriter();
    var result = await CreateService(client, writer, maxPages: 2).SyncAsync(CancellationToken.None);
    Check(!result.Completed && result.IncompleteReason == GlobalDealSweepIncompleteReason.PageBudgetExhausted,
        "the page budget bounds the sweep");
    Check(writer.Calls == 0, "budget exhaustion never writes, so the previous snapshot is preserved");
}

{
    var client = new ScriptedGlobalDealClient(_ => new GlobalDealPage(
        [Deal("a", official: true), Deal("b", official: false)],
        false,
        null));
    var writer = new RecordingGlobalDealWriter();
    var result = await CreateService(client, writer).SyncAsync(CancellationToken.None);
    Check(result.Completed && result.Deals == 2, "the sweep observes every deal");
    Check(writer.Last is { Count: 1 } && writer.Last[0].DealKey == "a",
        "only official deals are persisted; a non-allowlisted shop is dropped");
}

{
    var client = new ScriptedGlobalDealClient(_ => new GlobalDealPage([], false, null));
    var writer = new RecordingGlobalDealWriter();
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await CheckThrows<OperationCanceledException>(
        () => CreateService(client, writer).SyncAsync(cancelled.Token),
        "caller cancellation propagates out of the service");
    Check(writer.Calls == 0, "cancellation never writes");
}

// --- Pure policy, currency and wishlist rules -------------------------------

var fxDate = new DateOnly(2026, 10, 17);
var mxn = GlobalDealCurrency.Project("MXN", 5000, null);
Check(mxn is { PricingType: GlobalDealPricingTypes.Regional, MxnCurrentPriceMinor: 5000 },
    "an MXN quote is regional and keeps its amount");
var fx = GlobalDealCurrency.Project("USD", 1000, new GlobalDealFxRate(18.5m, fxDate, "banxico"));
Check(fx is { PricingType: GlobalDealPricingTypes.FxEstimate, MxnCurrentPriceMinor: 18500 } &&
      fx.FxRate == 18.5m && fx.FxRateDate == fxDate && fx.FxSource == "banxico",
    "a USD quote with a persisted rate becomes a labelled fx_estimate");
var unconvertedUsd = GlobalDealCurrency.Project("USD", 1000, null);
Check(unconvertedUsd is { PricingType: GlobalDealPricingTypes.Unconverted, MxnCurrentPriceMinor: null },
    "a USD quote without a rate makes no MXN claim");
var unconvertedEur = GlobalDealCurrency.Project("EUR", 1000, new GlobalDealFxRate(18.5m, fxDate, "banxico"));
Check(unconvertedEur.PricingType == GlobalDealPricingTypes.Unconverted,
    "a non-USD foreign quote is never converted through the USD rate");

Check(GlobalDealPolicy.DiscountPercent(null, 5000, 10000) == 50,
    "a missing cut falls back to regular-vs-current arithmetic");
Check(GlobalDealPolicy.DiscountPercent(67, 5000, 10000) == 67, "a coherent cut is used as-is");
Check(GlobalDealPolicy.DiscountPercent(150, 5000, 10000) == 50,
    "an out-of-range cut is ignored instead of being trusted");
Check(GlobalDealPolicy.DiscountPercent(null, 5000, null) is null,
    "no cut and no regular price means no fabricated discount");

Check(GlobalDealPolicy.GoodDealReason(
        "game", GlobalDealPricingTypes.Unconverted, 90, 100, "USD", null, null, 50) is null,
    "an unconverted foreign price is not good-deal eligible");
Check(GlobalDealPolicy.GoodDealReason(
        "game", GlobalDealPricingTypes.Regional, 60, 100, "MXN", null, null, 50) == GlobalDealSignals.Discount,
    "a discount at or above the threshold qualifies");
Check(GlobalDealPolicy.GoodDealReason(
        "game", GlobalDealPricingTypes.Regional, 10, 500, "USD", 600, "USD", 50) == GlobalDealSignals.HistoryLow,
    "a same-currency history low qualifies even below the discount threshold");
Check(GlobalDealPolicy.GoodDealReason(
        "game", GlobalDealPricingTypes.Regional, 10, 500, "USD", 400, "EUR", 50) is null,
    "a history low in another currency never matches");
Check(GlobalDealPolicy.GoodDealReason(
        "game", GlobalDealPricingTypes.Regional, null, 100, "MXN", null, null, 50) is null,
    "a deal with no discount and no low has no fabricated reason");
Check(GlobalDealPolicy.GoodDealReason(
        "dlc", GlobalDealPricingTypes.Regional, 90, 100, "MXN", null, null, 50) is null,
    "a non-game item (dlc/package/null) is not part of the games feed");
Check(GlobalDealPolicy.IsEligibleGameType("game") && !GlobalDealPolicy.IsEligibleGameType("dlc") &&
      !GlobalDealPolicy.IsEligibleGameType(null),
    "only the game type is eligible for the feed");

Check(GlobalDealPolicy.IsPricingComparable(GlobalDealPricingTypes.Regional) &&
      GlobalDealPolicy.IsPricingComparable(GlobalDealPricingTypes.FxEstimate),
    "regional and fx_estimate are the only price-eligible classifications");
Check(!GlobalDealPolicy.IsPricingComparable(GlobalDealPricingTypes.Unconverted),
    "unconverted is not price-eligible");

// Wishlist exclusion is exact and scoped to the current user.
var libraryRows = new[]
{
    new GlobalDealWishlistLibraryRow(1, "steam", "wished", "100"),
    new GlobalDealWishlistLibraryRow(1, "steam", "wished", "200"),
    new GlobalDealWishlistLibraryRow(2, "steam", "wished", "300"),
    new GlobalDealWishlistLibraryRow(1, "steam", "owned", "400")
};
var gameRows = new[]
{
    new GlobalDealWishlistGameRow(100, "Halo", null, "mx"),
    new GlobalDealWishlistGameRow(200, "Doom", "uuid-200", "mx"),
    new GlobalDealWishlistGameRow(300, "Other", "uuid-300", "mx"),
    new GlobalDealWishlistGameRow(100, "Halo US", "uuid-us", "us")
};
var resolved = GlobalDealWishlist.Resolve(1, libraryRows, gameRows);
Check(resolved.Count == 2, "only the current user's wished Steam rows resolve");
Check(resolved.All(entry => entry.AppId is 100 or 200), "another user's wishlist and non-wished states are dropped");
var wishedItadIds = GlobalDealWishlist.ItadIds(resolved);
Check(wishedItadIds.Count == 1 && wishedItadIds.Contains("uuid-200"),
    "only the MX catalog ITAD id is a wishlist identity");
Check(!GlobalDealWishlist.IsOutsideWishlist("uuid-200", wishedItadIds),
    "a wished ITAD game is excluded from the feed");
Check(GlobalDealWishlist.IsOutsideWishlist("uuid-999", wishedItadIds),
    "a non-wished ITAD game stays in the feed");
// Honest unknown: a wished game with no ITAD identity cannot be excluded, so a deal that happens to
// belong to it is NOT provably non-wishlist and would stay visible. It is an unknown match, not a
// proven non-wishlist game.
Check(GlobalDealWishlist.IsOutsideWishlist(null, wishedItadIds),
    "a deal with no ITAD identity is never excluded by a guess");
Check(resolved.Any(entry => entry.AppId == 100 && entry.ItadGameId is null),
    "a wished game without an ITAD id resolves with an unknown identity");
Check(GlobalDealWishlist.ParseAppId("not-a-number") == 0, "a non-numeric appid is not an identity");

// Pagination bounds.
Check(GlobalDealCatalogPager.NormalizePage(null) == 1 && GlobalDealCatalogPager.NormalizePage(-3) == 1,
    "an invalid page falls back to page 1");
Check(GlobalDealCatalogPager.NormalizePageSize(GlobalDealCatalogPager.MaxPageSize + 100) ==
      GlobalDealCatalogPager.MaxPageSize,
    "an oversized page size is clamped");

// --- Isolation --------------------------------------------------------------

Check(GlobalDealSources.GlobalDeals != "itad",
    "the deals source is isolated from the per-game itad orphan purge");
Check(GlobalDealSources.GlobalDeals != "itad_global",
    "the deals source is isolated from the global bundle sweep source");
Check(GlobalDealSyncService.AdvisoryLockKey != 7_831_442_091L,
    "the deals sweep lock is distinct from the wishlist sync lock");
Check(GlobalDealSyncService.AdvisoryLockKey != 7_831_442_092L,
    "the deals sweep lock is distinct from the ntfy deals lock");
Check(GlobalDealSyncService.AdvisoryLockKey != 7_831_442_093L,
    "the deals sweep lock is distinct from the global bundle sweep lock");

// --- Report -----------------------------------------------------------------

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} global deal checks failed");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"  - {failure}");
    }

    Environment.Exit(1);
}

Console.WriteLine("PASS: global deals parser, sweep and policy checks");

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

internal sealed class ScriptedGlobalDealClient(Func<int, GlobalDealPage> page) : IGlobalDealClient
{
    public Exception? Failure { get; set; }

    public Task<GlobalDealPage> GetDealsPageAsync(
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
}

internal sealed class RecordingGlobalDealWriter : IGlobalDealSnapshotWriter
{
    public int Calls { get; private set; }
    public IReadOnlyList<ItadGlobalDeal>? Last { get; private set; }

    public Task<GlobalDealWriteResult> ReplaceAsync(
        IReadOnlyList<ItadGlobalDeal> deals,
        DateTime observedAt,
        CancellationToken cancellationToken)
    {
        Calls++;
        Last = deals;
        return Task.FromResult(new GlobalDealWriteResult(deals.Count, 0));
    }
}
