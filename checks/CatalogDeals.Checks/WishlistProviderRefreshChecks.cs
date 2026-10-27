#nullable enable
using System.Net;
using System.Text.Json;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Catalog;
using Deals.BusinessLogic.Models.Diagnostics;
using Deals.BusinessLogic.Models.GgDeals;
using Deals.BusinessLogic.Models.Itad;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Models.Stores;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Internal;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The provider refresh pass that the wishlist price job runs per game
/// (<c>SteamGameService.RefreshProvidersAsync</c> → <see cref="GameRefreshMode.BackgroundProviders"/>):
/// what the stores answered must reach the database.
///
/// <para>
/// This is the regression for the reported <c>wishlist.syncprices</c> cycle: 604 Steam successes and 2168
/// provider failures with zero successes. The pipeline asked every due store, the stores answered, and then
/// the cached-GET fast return answered from the stored snapshot <em>after</em> the provider calls and
/// <em>before</em> persistence, so every authoritative answer was thrown away, the provider timestamps never
/// advanced, and the job's counters — which classify a provider by comparing its stamp before and after the
/// pass — could only read "failed".
/// </para>
///
/// <para>
/// Everything runs offline against an in-memory <see cref="AppDbContext"/>: the real
/// <see cref="Repository"/>, the real <see cref="SteamGameService"/> and the real
/// <see cref="WishlistSyncService"/> run, and only the outbound clients are doubles — so "the store was
/// called" and "nothing was persisted" are observations, not promises. No connection is opened, no HTTP is
/// sent, and nothing here is a PostgreSQL behaviour claim.
/// </para>
///
/// <para>
/// The direct-store fixtures have no canonical <c>game_id</c> on purpose: with one, the store write first
/// claims its external id through <c>GameIdentityClaimer</c>'s <c>INSERT … ON CONFLICT</c>, and raw SQL is not
/// something the in-memory provider can execute. The claim guard is therefore not exercised here; it is
/// untouched by this fix and covered by <c>SQL/checks</c> plus the build.
/// </para>
///
/// <para>
/// The same limitation bounds the wishlist-cycle scenario: that pass finishes each game by writing the
/// refreshed title with a set-based <c>ExecuteUpdate</c>, which only a relational provider can run, so only its
/// per-provider telemetry is asserted — it is recorded per phase, before that statement. The game-level
/// <c>Refreshed</c>/<c>Failed</c> counters sit beyond it and are not claimed here.
/// </para>
/// </summary>
internal static class WishlistProviderRefreshChecks
{
    private const int AppId = 1057800;
    private static readonly List<string> Failures = [];

    // The service gates the Steam cache and every provider window against DateTime.UtcNow, so the fixture is
    // built relative to the real clock: a pinned date would be "stale" on most machines and silently turn the
    // cached-GET case into a Steam fetch.
    private static DateTime Now => DateTime.UtcNow;

    public static void Run()
    {
        BackgroundAnswerIsPersisted();
        BackgroundNoMatchAdvancesItsOwnStamp();
        BackgroundFailureKeepsItsStamp();
        CachedSteamOnlyGetWritesNothing();
        StalePriceBatchPersistsItsDuePhases();
        StalePriceBatchWithFreshSteamStillPersistsProviders();
        WishlistCountersReportTheProviderSuccess();

        // The safe per-phase diagnosis of the same pass (spec S2/S3): which provider phase of which game
        // refreshed, had nothing to offer or failed, and why — with a fixed set of reasons instead of a message.
        StoreAnswerIsDiagnosed();
        StoreNoMatchIsDiagnosed();
        HttpStatusesReachTheReport();
        TransportFailureWithoutStatusIsInvented();
        TimeoutAndParseAreNamed();
        CallerCancellationIsAnOutcomeOfItsOwn();
        IdentityPrerequisiteFailureNamesTheFallbackGate();
        NonComparableGameIsNotAnOutage();
        SteamPhaseIsDiagnosed();
        AnInteractiveReadRecordsNothing();
        AJobRunClaimsTheCollector();

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL: {Failures.Count} provider refresh checks failed");
            foreach (var failure in Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            Environment.Exit(1);
        }

        Console.WriteLine("PASS: wishlist provider refresh checks");
    }

    private static void Check(bool value, string name)
    {
        if (!value) Failures.Add(name);
    }

    private static void CheckEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Failures.Add($"{name}: expected '{expected}', got '{actual}'");
        }
    }

    /// <summary>
    /// The bug. One due direct store over an existing Steam snapshot answers with an offer: the offer and the
    /// store's own stamp must be persisted, no Steam request may be made (this mode never fetches Steam), and
    /// the caller must receive the persisted snapshot.
    ///
    /// <para>
    /// RED before the fix: the store is called exactly once (the phase ran), the returned model still carries
    /// a null store stamp and the database holds no offer for it, because the cached-GET fast return answered
    /// from the pre-refresh snapshot after the provider call.
    /// </para>
    /// </summary>
    private static void BackgroundAnswerIsPersisted()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background);
        var before = harness.Snapshot();

        var details = harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(details is not null, "a background pass over an existing snapshot answers with details");
        CheckEqual(1, harness.Epic.ByTitleCalls, "the due store phase really called the store");

        // What the caller sees: this is what the wishlist job's counters classify.
        Check(details?.EpicRefreshedAt is not null,
            "the returned snapshot carries the stamp of the store that answered");
        Check(details?.EpicRefreshedAt is not null &&
            (before.EpicRefreshedAt is null || details.EpicRefreshedAt > before.EpicRefreshedAt),
            "the returned store stamp advanced over the stored one");

        // What was persisted: the whole point of a price pass.
        var after = harness.Snapshot();
        Check(after.EpicRefreshedAt is not null,
            "the store stamp of an authoritative background answer is persisted");
        Check(after.EpicRefreshedAt is not null &&
            (before.EpicRefreshedAt is null || after.EpicRefreshedAt > before.EpicRefreshedAt),
            "the persisted store stamp advanced");
        Check(before.EpicRefreshedAt is null, "the store started the scenario never refreshed");
        CheckEqual(1, after.EpicOffers.Count, "the price the store answered is persisted as an offer");
        var offer = after.EpicOffers.FirstOrDefault();
        CheckEqual(16199, offer?.OriginalCurrentPriceMinor,
            "the persisted offer keeps the store's own price");
        CheckEqual("MXN", offer?.OriginalCurrency, "the persisted offer keeps the store's own currency");
        CheckEqual("Epic Games Store", offer?.ShopName, "the persisted offer keeps the store's own name");

        // Untouched neighbours: one store's answer never moves another provider's window.
        CheckEqual(before.OffersRefreshedAt, after.OffersRefreshedAt,
            "a due store leaves the ITAD window alone");
        CheckEqual(before.GgDealsRefreshedAt, after.GgDealsRefreshedAt,
            "a due store leaves the gg.deals window alone");
        CheckEqual(before.MicrosoftRefreshedAt, after.MicrosoftRefreshedAt,
            "a due store leaves the other store window alone");
        CheckEqual(0, harness.Steam.Calls, "the background mode never requests the Steam store");
        CheckEqual(0, harness.Microsoft.HttpCalls, "a store that is not due is never called");
        Check(harness.Saves.During > 0, "the provider answer reaches the database through a save");
    }

    /// <summary>
    /// An authoritative "this store does not sell it" is an answer too: it advances that store's stamp so a
    /// later cycle does not ask again, without writing any offer. Under the bug this answer was discarded as
    /// well, so the game stayed permanently due.
    /// </summary>
    private static void BackgroundNoMatchAdvancesItsOwnStamp()
    {
        using var harness = Harness.Create(EpicAnswer.NoMatch, StampShape.Background);
        var before = harness.Snapshot();

        var details = harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None)
            .GetAwaiter().GetResult();

        CheckEqual(1, harness.Epic.ByTitleCalls, "the no-match cycle really asked the store");
        Check(details?.EpicRefreshedAt is not null,
            "an authoritative no-match stamps the store that answered it");

        var after = harness.Snapshot();
        Check(after.EpicRefreshedAt is not null,
            "an authoritative no-match advances the persisted stamp");
        Check(after.EpicRefreshedAt is not null &&
            (before.EpicRefreshedAt is null || after.EpicRefreshedAt > before.EpicRefreshedAt),
            "the no-match stamp moved forward");
        CheckEqual(0, after.EpicOffers.Count, "an authoritative no-match writes no offer for that store");
        CheckEqual(before.OffersRefreshedAt, after.OffersRefreshedAt,
            "a store no-match never touches the ITAD window");
    }

    /// <summary>
    /// A failed call is not an answer: the store's stamp stays exactly where it was, so the next cycle retries,
    /// and no offer is written. This must stay true after the fix — the fast return used to be the only thing
    /// keeping the stamp still, and it did so by discarding everything.
    /// </summary>
    private static void BackgroundFailureKeepsItsStamp()
    {
        using var harness = Harness.Create(EpicAnswer.Failed, StampShape.Background);
        var before = harness.Snapshot();

        harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None)
            .GetAwaiter().GetResult();

        CheckEqual(1, harness.Epic.ByTitleCalls, "the failed cycle really asked the store");
        var after = harness.Snapshot();
        CheckEqual(before.EpicRefreshedAt, after.EpicRefreshedAt,
            "a failed store call leaves its stamp untouched so the next cycle retries");
        CheckEqual(0, after.EpicOffers.Count, "a failed store call writes no offer");
        CheckEqual(before.OffersRefreshedAt, after.OffersRefreshedAt,
            "a failed store call does not degrade the ITAD window");
    }

    /// <summary>
    /// The interactive detail GET keeps its cache: a snapshot younger than the Steam window is answered from
    /// the database with no Steam call, no provider call and no save at all. This is the shape the bug
    /// over-applied; it must survive the fix untouched.
    /// </summary>
    private static void CachedSteamOnlyGetWritesNothing()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background);
        var before = harness.Snapshot();
        harness.Saves.Reset();

        var details = harness.Service.GetByAppIdAsync(AppId, forceRefresh: false, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(details is not null, "a cached detail GET still answers with the stored snapshot");
        CheckEqual(0, harness.Steam.Calls, "a cached detail GET never calls the Steam store");
        CheckEqual(0, harness.Epic.ByTitleCalls, "a cached detail GET never calls a provider store");
        CheckEqual(0, harness.Itad.Lookups, "a cached detail GET never calls ITAD");
        CheckEqual(0, harness.Saves.During,
            "a cached detail GET writes nothing (no transaction, no save)");

        var after = harness.Snapshot();
        CheckEqual(before.EpicRefreshedAt, after.EpicRefreshedAt,
            "a cached detail GET leaves the stored provider stamps alone");
        CheckEqual(0, after.EpicOffers.Count, "a cached detail GET writes no offer");
    }

    /// <summary>
    /// The manual pending-price pass was already correct and must stay correct: every due price phase of the
    /// 72-hour window answers and is persisted — the Steam direct snapshot included — and the typed per-phase
    /// outcome reports the phases that really advanced.
    /// </summary>
    private static void StalePriceBatchPersistsItsDuePhases()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.BatchStale);
        var before = harness.Snapshot();

        var result = harness.Service.RefreshStalePricesAsync(AppId, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(result.Ran, "the batch ran on a stored snapshot");
        Check(result.SteamDue, "a snapshot older than 72 h is due for the Steam direct phase");
        Check(result.AdvancedPhases.Contains(SteamStalePricePhases.Steam),
            "the batch reports its Steam phase as persisted");
        Check(result.AdvancedPhases.Contains(SteamStalePricePhases.Itad),
            "the batch reports the ITAD phase it received");
        Check(result.AdvancedPhases.Contains(SteamStalePricePhases.GgDeals),
            "the batch reports the gg.deals phase it received");
        Check(!result.Degraded, "an all-answering batch is not degraded");
        CheckEqual(1, harness.Steam.Calls, "the batch does request the Steam store");

        var after = harness.Snapshot();
        Check(after.ObservedAt > before.ObservedAt, "the batch persists the fresh Steam snapshot");
        Check(after.OffersRefreshedAt > before.OffersRefreshedAt,
            "the batch persists the ITAD stamp it received");
        Check(after.GgDealsRefreshedAt > before.GgDealsRefreshedAt,
            "the batch persists the gg.deals stamp it received");
        CheckEqual(before.EpicRefreshedAt, after.EpicRefreshedAt,
            "a store that is not due keeps its stamp in the batch");
    }

    /// <summary>
    /// The batch's own guard, stated as it matters: a snapshot whose Steam price is still inside the 72-hour
    /// window is <em>not</em> re-fetched, so the pass reaches persistence with no fresh Steam payload — and the
    /// due provider answers must still be written. That is the case the old shared fast return had to
    /// special-case for the batch, and the one an over-broad removal of it would break.
    /// </summary>
    private static void StalePriceBatchWithFreshSteamStillPersistsProviders()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.BatchFreshSteam);
        var before = harness.Snapshot();

        var result = harness.Service.RefreshStalePricesAsync(AppId, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(result.Ran, "the batch ran with a fresh Steam price");
        Check(!result.SteamDue, "a Steam price inside the 72-hour window is not re-requested");
        CheckEqual(0, harness.Steam.Calls, "so the Steam store is not called");
        Check(result.AdvancedPhases.Contains(SteamStalePricePhases.Epic),
            "the batch reports the store phase it received");
        Check(!result.Degraded, "the store answering is not degradation");

        var after = harness.Snapshot();
        Check(after.EpicRefreshedAt is not null,
            "a due store answer persists even when no Steam payload was fetched");
        CheckEqual(1, after.EpicOffers.Count, "and so does its price");
        CheckEqual(before.ObservedAt, after.ObservedAt, "the fresh Steam snapshot is not rewritten");
    }

    /// <summary>
    /// The reported symptom, at the level the user reads it: the wishlist price job's own per-provider
    /// counters. The job classifies a provider by comparing the stamp before and after the pass, so with the
    /// discarded answer it could only report <c>Attempted 1 / Succeeded 0 / Failed 1</c> per game — the
    /// "2168 fails, 0 success" of the report — no matter how many stores answered.
    /// </summary>
    private static void WishlistCountersReportTheProviderSuccess()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background, wishedRow: true);

        var sync = new WishlistSyncService(
            harness.Repository,
            new ThrowingWishlistClient(),
            harness.Service,
            new WishlistSyncSettings(MaxRefreshesPerHour: 600_000, RefreshAfterDays: 7,
                MissingMetadataDelayMilliseconds: 0),
            NullLogger<WishlistSyncService>.Instance);

        var report = sync.RefreshWishedGamesAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Only the provider-level telemetry is asserted: it is recorded per phase, before the pass writes the
        // refreshed title with a set-based ExecuteUpdate that only a relational provider can run. The
        // game-level Refreshed/Failed counters sit on the far side of that statement, so they are JSON-report
        // territory, not something an in-memory check can prove.
        Check(report.Providers is not null, "the cycle reports per-provider telemetry");
        var epic = report.Providers is not null && report.Providers.TryGetValue("epic", out var counters)
            ? counters
            : null;
        Check(epic is not null, "the cycle reports the store it asked");
        CheckEqual(1, epic?.Attempted, "the store phase is counted as attempted");
        CheckEqual(1, epic?.Succeeded,
            "an authoritative store answer is counted as a success, not a failure");
        CheckEqual(0, epic?.Failed, "the store phase is not counted as failed");
    }

    // ------------------------------------------------------------------ diagnosis ---------------------------------
    //
    // The safe per-game diagnosis of the same pass (spec S2/S3). Every assertion below is about the document that
    // reaches job_runs.details: a closed outcome, a closed reason, the status only when the exception carried one,
    // the exception TYPE and never its message, URL or payload. The pipeline's own behaviour (stamps, offers,
    // saves) is asserted by the scenarios above and is not re-read here.

    private static void StoreAnswerIsDiagnosed()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background);
        harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None).GetAwaiter().GetResult();

        var snapshot = harness.Diagnostics.BuildSnapshot();
        Check(snapshot.SchemaVersion == 1, "the report declares its schema version");
        CheckEqual(4, snapshot.TotalEvents, "the four phases of the cycle are reported once each");
        CheckEqual(4, snapshot.ShownEvents, "nothing is truncated in a four-event run");
        CheckEqual(0, snapshot.TruncatedEvents, "a bounded run reports no truncation");
        CheckEqual(4, snapshot.Summary.Sum(row => row.Count), "the summary counts every recorded event");

        var epic = EventFor(harness, "epic");
        Check(epic?.Outcome == "refreshed", "the store that answered is a refreshed phase");
        Check(epic?.Reason == "refreshed", "a refreshed phase carries the refreshed reason");
        CheckEqual<int?>(AppId, epic?.AppId, "the event names the game it belongs to");
        Check(epic?.ElapsedMs is >= 0, "the phase reports how long it took");

        // The identity-only ITAD read is due here (the direct store has no stored id yet), and answers
        // "not found": an answer, reported as missing identity rather than as a failure.
        var identity = EventFor(harness, "itad-identity");
        Check(identity?.Outcome == "no_match", "the ITAD identity read that answered nothing is a no-match");
        Check(identity?.Reason == "no_identity", "and its reason is the missing identity");

        foreach (var provider in new[] { "ggdeals", "microsoft" })
        {
            var skipped = EventFor(harness, provider);
            Check(skipped?.Outcome == "skipped", $"{provider} was not asked and is reported as skipped");
            Check(skipped?.Reason == "gate_skipped", $"{provider} names the window gate that skipped it");
        }

        Check(EventFor(harness, "steam") is null, "the provider-only mode never reports a Steam phase");
    }

    private static void StoreNoMatchIsDiagnosed()
    {
        using var harness = Harness.Create(EpicAnswer.NoMatch, StampShape.Background);
        harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None).GetAwaiter().GetResult();

        var epic = EventFor(harness, "epic");
        Check(epic?.Outcome == "no_match", "an authoritative no-match is not a failure");
        Check(epic?.Reason == "store_no_offer", "the reason names the store's own answer");
        Check(epic?.HttpStatus is null, "a no-match claims no HTTP status");
        Check(epic?.ErrorType is null, "a no-match names no exception");
    }

    private static void HttpStatusesReachTheReport()
    {
        AssertStoreFailure(
            new HttpRequestException("SYNTHETIC-SECRET https://api.gg.deals/v1/?key=abc", null,
                HttpStatusCode.TooManyRequests),
            "http_error",
            429,
            "a 429 that ended the phase is reported with its own status");
        AssertStoreFailure(
            new HttpRequestException("SYNTHETIC-SECRET https://store.epicgames.com/graphql", null,
                HttpStatusCode.Forbidden),
            "http_error",
            403,
            "the store's bot-challenge 403 is reported with its own status");
        AssertStoreFailure(
            new HttpRequestException("SYNTHETIC-SECRET", null, HttpStatusCode.BadGateway),
            "http_error",
            502,
            "a 5xx keeps its status instead of being folded into the others");
    }

    private static void TransportFailureWithoutStatusIsInvented() =>
        AssertStoreFailure(
            new HttpRequestException("SYNTHETIC-SECRET connection refused"),
            "transport_error",
            null,
            "a request that never got a response reports transport_error and no invented status");

    private static void TimeoutAndParseAreNamed()
    {
        AssertStoreFailure(new TaskCanceledException("SYNTHETIC-SECRET timeout"), "timeout", null,
            "the HttpClient timeout is reported as a timeout");
        AssertStoreFailure(new JsonException("SYNTHETIC-SECRET payload"), "parse_error", null,
            "an unreadable payload is reported as a parse error");
    }

    private static void CallerCancellationIsAnOutcomeOfItsOwn()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background);
        using var cancellation = new CancellationTokenSource();
        harness.Epic.Override = () =>
        {
            cancellation.Cancel();
            return Task.FromException<StoreOffer?>(new OperationCanceledException(cancellation.Token));
        };

        try
        {
            harness.Service.RefreshProvidersAsync(AppId, cancellation.Token).GetAwaiter().GetResult();
            Check(false, "a cancellation asked for by the caller must still abort the request");
        }
        catch (OperationCanceledException)
        {
            // Expected: the phase rethrows what the caller caused.
        }

        var epic = EventFor(harness, "epic");
        Check(epic?.Outcome == "cancelled", "a caller cancellation is its own outcome, not a failed store");
        Check(epic?.Reason == "cancelled", "the reason is the cancellation itself");
        Check(epic?.HttpStatus is null, "a cancellation claims no status");
    }

    private static void IdentityPrerequisiteFailureNamesTheFallbackGate()
    {
        using var harness = Harness.Create(EpicAnswer.NoMatch, StampShape.Background);
        harness.Itad.Failure = new HttpRequestException(
            "SYNTHETIC-SECRET ITAD down", null, HttpStatusCode.TooManyRequests);

        harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None).GetAwaiter().GetResult();

        var identity = EventFor(harness, "itad-identity");
        Check(identity?.Outcome == "failed", "the degraded identity prerequisite is a failed phase");
        Check(identity?.Reason == "http_error", "its reason is the HTTP failure it absorbed");

        var epic = EventFor(harness, "epic");
        Check(epic?.Outcome == "no_match", "the store answered nothing because it had no identity");
        Check(epic?.Reason == "title_fallback_disabled",
            "and the reason says the title fallback was refused, not that the store lacks the game");
        CheckEqual(0, harness.Epic.ByTitleCalls, "the refused fallback really never searched the store");
    }

    private static void NonComparableGameIsNotAnOutage()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background, comparable: false);
        harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None).GetAwaiter().GetResult();

        foreach (var provider in new[] { "itad", "ggdeals", "epic", "microsoft" })
        {
            var skipped = EventFor(harness, provider);
            Check(skipped?.Outcome == "skipped", $"{provider} is not asked for a non-comparable item");
            Check(skipped?.Reason == "not_comparable", $"{provider} names comparability, not an outage");
            Check(skipped?.HttpStatus is null, $"{provider} claims no status for a decision");
        }

        CheckEqual(0, harness.Epic.ByTitleCalls, "a non-comparable item is never searched");
    }

    private static void SteamPhaseIsDiagnosed()
    {
        // The batch owns the Steam direct phase: a snapshot past the chosen window is re-fetched and reported.
        using var stale = Harness.Create(EpicAnswer.Offer, StampShape.BatchStale);
        stale.Service.RefreshStalePricesAsync(AppId, CancellationToken.None).GetAwaiter().GetResult();

        var steam = EventFor(stale, "steam");
        Check(steam?.Outcome == "refreshed", "the batch's Steam phase is reported");
        Check(steam?.Reason == "refreshed", "a refreshed Steam phase says so");
        CheckEqual<int?>(AppId, steam?.AppId, "the Steam event names the appid");
        Check(EventFor(stale, "itad")?.Reason == "no_identity",
            "an ITAD no-match is reported as missing identity, not as a failure");
        Check(EventFor(stale, "ggdeals")?.Reason == "no_identity",
            "a gg.deals no-match is reported the same way");

        // Inside the chosen window Steam is not re-asked: that is a gate, and the report says which one.
        using var fresh = Harness.Create(EpicAnswer.Offer, StampShape.BatchFreshSteam);
        fresh.Service.RefreshStalePricesAsync(AppId, CancellationToken.None).GetAwaiter().GetResult();
        var freshSteam = EventFor(fresh, "steam");
        Check(freshSteam?.Outcome == "skipped", "a fresh Steam price is a skipped phase");
        Check(freshSteam?.Reason == "gate_skipped", "the skip names the window gate");
        CheckEqual(0, fresh.Steam.Calls, "the gate really prevented the Steam call");

        // A game the batch asks for but that has no persisted snapshot is declined before any phase runs.
        using var declinedHarness = Harness.Create(EpicAnswer.Offer, StampShape.Background);
        var declined = declinedHarness.Service
            .RefreshStalePricesAsync(999_999, CancellationToken.None).GetAwaiter().GetResult();
        Check(!declined.Ran, "the plan declined a game with no snapshot");
        var declinedEvent = EventFor(declinedHarness, "steam");
        Check(declinedEvent?.Outcome == "skipped", "a declined game is a skipped phase");
        Check(declinedEvent?.Reason == "gate_declined",
            "and it is distinguished from a window that is simply fresh");
        CheckEqual<int?>(999_999, declinedEvent?.AppId, "the declination still names the appid that was asked for");
    }

    /// <summary>
    /// An interactive detail read reaches the provider phases exactly as before but records nothing (spec S2): its
    /// scope's collector was never claimed by a job, so no event and no warning leaves it.
    /// </summary>
    private static void AnInteractiveReadRecordsNothing()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background, jobRun: false);

        // A cached interactive GET: it still touches the Steam gate, which is exactly the phase that must stay silent.
        var details = harness.Service.GetByAppIdAsync(AppId, forceRefresh: false, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check(details is not null, "the interactive read still answers");
        Check(!harness.Diagnostics.IsActive, "an interactive request never claims the collector");
        CheckEqual(0, harness.Diagnostics.BuildSnapshot().TotalEvents,
            "an interactive read records no phase, not even the ones it ran");
        CheckEqual(0, harness.Diagnostics.BuildSnapshot().Summary.Count,
            "and it produces no aggregate that could be mistaken for a job report");
    }

    /// <summary>
    /// The same pipeline over a collector a job claimed: the phases are recorded, which is what proves the silence
    /// above comes from the claim and not from the pipeline having stopped reporting.
    /// </summary>
    private static void AJobRunClaimsTheCollector()
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background);

        harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None).GetAwaiter().GetResult();

        Check(harness.Diagnostics.IsActive, "a job run claims its collector");
        Check(harness.Diagnostics.BuildSnapshot().TotalEvents > 0,
            "and the same phases that stay silent for a page are recorded for the job");
    }

    /// <summary>
    /// One failing store phase, asserted on the document only: the closed reason, the status when the exception
    /// really carries one, the exception type, and the absence of anything the exception message could smuggle in.
    /// </summary>
    private static void AssertStoreFailure(
        Exception failure,
        string expectedReason,
        int? expectedStatus,
        string description)
    {
        using var harness = Harness.Create(EpicAnswer.Offer, StampShape.Background);
        harness.Epic.Override = () => Task.FromException<StoreOffer?>(failure);

        harness.Service.RefreshProvidersAsync(AppId, CancellationToken.None).GetAwaiter().GetResult();

        var epic = EventFor(harness, "epic");
        Check(epic?.Outcome == "failed", $"{description}: the phase is a failure");
        Check(epic?.Reason == expectedReason, $"{description}: expected reason '{expectedReason}', got '{epic?.Reason}'");
        CheckEqual(expectedStatus, epic?.HttpStatus, $"{description}: status");
        Check(epic?.ErrorType == failure.GetType().Name, $"{description}: the exception type is named");

        var document = JsonSerializer.Serialize(harness.Diagnostics.BuildSnapshot());
        Check(!document.Contains("SYNTHETIC-SECRET", StringComparison.Ordinal),
            $"{description}: no exception message reaches the report");
        Check(!document.Contains("api.gg.deals", StringComparison.Ordinal) &&
            !document.Contains("store.epicgames.com", StringComparison.Ordinal),
            $"{description}: no provider URL reaches the report");
    }

    private static IReadOnlyList<SyncDiagnosticEvent> Events(Harness harness) =>
        harness.Diagnostics.BuildSnapshot().Events;

    private static SyncDiagnosticEvent? EventFor(Harness harness, string provider) =>
        Events(harness).LastOrDefault(entry => entry.Provider == provider);

    private enum EpicAnswer { Offer, NoMatch, Failed }

    /// <summary>
    /// Which stamps the fixture starts with. All three shapes keep the same row and the same due store; only
    /// the ages differ, because the point of each scenario is a different window.
    /// </summary>
    private enum StampShape
    {
        /// <summary>Fresh Steam snapshot, fresh ITAD/gg.deals/Microsoft, the direct store never refreshed.</summary>
        Background,
        /// <summary>The 72-hour batch view: Steam and the ITAD/gg.deals stamps five days old.</summary>
        BatchStale,
        /// <summary>The batch with a Steam price inside its window, so no Steam payload is fetched.</summary>
        BatchFreshSteam
    }

    /// <summary>
    /// One offline scenario: an in-memory database with a single Steam snapshot, the real repository and the
    /// real service over double clients that count their calls instead of reaching the network.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly AppDbContext _context;

        private Harness(DbContextOptions<AppDbContext> options, AppDbContext context, SaveCounter saves,
            EpicAnswer epicAnswer)
        {
            _options = options;
            _context = context;
            Saves = saves;
            Epic = new FakeEpicStore { Answer = epicAnswer };
            Repository = new Repository(context);
            Service = new SteamGameService(
                Repository,
                Steam,
                Itad,
                GgDeals,
                Epic,
                new MicrosoftStoreClient(
                    new HttpClient(Microsoft),
                    new MicrosoftStoreClientSettings("MX", "es-mx", "MXN"),
                    new MicrosoftRequestGovernor()),
                new NullFxRateService(),
                new RepositoryIdentityResolver(Repository),
                NullLogger<SteamGameService>.Instance,
                new SteamOffersSettings(7),
                // The scoped per-run collector the job would inject: without it the pipeline produces no
                // diagnosis at all, which is the behaviour the interactive path keeps.
                Diagnostics);
        }

        public SyncDiagnosticsCollector Diagnostics { get; } = new();
        public FakeSteamStore Steam { get; } = new();
        public FakeItadClient Itad { get; } = new();
        public FakeGgDealsClient GgDeals { get; } = new();
        public FakeEpicStore Epic { get; }
        public CountingHttpHandler Microsoft { get; } = new();
        public SaveCounter Saves { get; }
        public Repository Repository { get; }
        public SteamGameService Service { get; }

        public static Harness Create(
            EpicAnswer epicAnswer,
            StampShape shape,
            bool wishedRow = false,
            bool comparable = true,
            bool jobRun = true)
        {
            var saves = new SaveCounter();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"wishlist-provider-refresh-{Guid.NewGuid():N}")
                // The pipeline opens one short persistence transaction. The in-memory store ignores it rather
                // than failing it, which is exactly the "no PostgreSQL semantics" caveat of this file.
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .AddInterceptors(saves)
                .Options;
            var context = new AppDbContext(options, new OfflineUser());
            var harness = new Harness(options, context, saves, epicAnswer);
            // A job run is the only thing that claims the collector (spec S2); an interactive request never does, so
            // the default for a page-shaped scenario is an unclaimed collector.
            if (jobRun)
            {
                harness.Diagnostics.Activate();
            }

            harness.Seed(shape, wishedRow, comparable);
            return harness;
        }

        private void Seed(StampShape shape, bool wishedRow, bool comparable)
        {
            var now = Now;
            var fresh = (DateTime?)now.AddMinutes(-5);
            var fiveDaysAgo = (DateTime?)now.AddDays(-5);

            var snapshot = new SteamGame
            {
                SteamGameId = 1,
                AppId = AppId,
                Name = "Floppy Knights",
                // A non-comparable item (a DLC, or a free game) is what the pipeline must never price.
                Type = comparable ? "game" : "dlc",
                IsFree = false,
                Currency = "MXN",
                InitialPriceMinor = 47999,
                CurrentPriceMinor = 47999,
                DiscountPercent = 0,
                Region = "mx",
                ObservedAt = shape == StampShape.BatchStale ? fiveDaysAgo!.Value : fresh!.Value,
                OffersRefreshedAt = shape == StampShape.Background ? fresh : fiveDaysAgo,
                GgDealsRefreshedAt = shape == StampShape.Background ? fresh : fiveDaysAgo,
                // The direct store under test: never refreshed, so it is due in every shape but the batch one,
                // which is about the Steam/ITAD/gg.deals phases and must not reach the store's id claim (raw
                // SQL, unsupported by the in-memory provider). Microsoft stays fresh so its client never runs —
                // its payload parsing is another check's subject.
                EpicRefreshedAt = shape == StampShape.BatchStale ? fresh : null,
                MicrosoftRefreshedAt = fresh
            };
            // Only the shapes that re-fetch Steam need a payload; the others must not call the client at all.
            Steam.Details = shape == StampShape.BatchStale
                ? new SteamGameDetails(AppId, "Floppy Knights", "game", false, "MXN", 47999, 35999, 25, "mx", now)
                : null;
            _context.SteamGames.Add(snapshot);

            if (wishedRow)
            {
                _context.UserLibrary.Add(new UserLibrary
                {
                    UserLibraryId = 1,
                    UserId = 1,
                    Store = "steam",
                    StoreGameId = AppId.ToString(),
                    Title = "Floppy Knights",
                    State = "wished",
                    ImportedAt = now.AddDays(-10)
                });
            }

            _context.SaveChanges();
        }

        /// <summary>The persisted snapshot, read through a second context so a write is real, not tracked.</summary>
        public Snapshot Snapshot()
        {
            _context.ChangeTracker.Clear();
            using var reader = new AppDbContext(_options, new OfflineUser());
            var game = reader.SteamGames.AsNoTracking().First(row => row.AppId == AppId && row.Region == "mx");
            var offers = reader.GameOffers.AsNoTracking()
                .Where(offer => offer.SteamGameId == game.SteamGameId)
                .ToList();
            return new Snapshot(
                game.ObservedAt,
                game.OffersRefreshedAt,
                game.GgDealsRefreshedAt,
                game.EpicRefreshedAt,
                game.MicrosoftRefreshedAt,
                offers.Where(offer => offer.Source == "epic").ToList());
        }

        public void Dispose() => _context.Dispose();
    }

    private sealed record Snapshot(
        DateTime ObservedAt,
        DateTime? OffersRefreshedAt,
        DateTime? GgDealsRefreshedAt,
        DateTime? EpicRefreshedAt,
        DateTime? MicrosoftRefreshedAt,
        IReadOnlyList<GameOffer> EpicOffers);

    private sealed class FakeSteamStore : ISteamStoreClient
    {
        public int Calls { get; private set; }
        public SteamGameDetails? Details { get; set; }

        public Task<IReadOnlyList<SteamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a price pass never searches the Steam store");

        public Task<SteamGameDetails?> GetAppDetailsAsync(int appId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Details);
        }
    }

    /// <summary>
    /// ITAD is the identity resolver of the direct stores: this fixture has no canonical anchor, so the
    /// identity-only read is due and answers "not found", which is an answer (no price, no stamp). When the
    /// batch's ITAD window is due instead, the price read follows and answers an empty deal list.
    /// </summary>
    private sealed class FakeItadClient : IItadClient
    {
        public int Lookups { get; private set; }
        public int PriceCalls { get; private set; }

        /// <summary>When set, the identity lookup fails with it instead of answering "not found".</summary>
        public Exception? Failure { get; set; }

        public Task<string?> LookupSteamAppIdAsync(
            int appId,
            CancellationToken cancellationToken,
            ItadRequestPriority priority = ItadRequestPriority.Background)
        {
            Lookups++;
            return Failure is not null
                ? Task.FromException<string?>(Failure)
                : Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<ItadGamePrices>> GetPricesAsync(
            IReadOnlyCollection<string> itadIds,
            CancellationToken cancellationToken,
            ItadRequestPriority priority = ItadRequestPriority.Background)
        {
            PriceCalls++;
            return Task.FromResult<IReadOnlyList<ItadGamePrices>>([]);
        }

        public Task<IReadOnlyList<ItadBundle>> GetBundlesAsync(
            IReadOnlyCollection<string> itadIds,
            CancellationToken cancellationToken,
            ItadRequestPriority priority = ItadRequestPriority.Background) =>
            throw new InvalidOperationException("this fixture never reaches a price-carrying ITAD phase");

        public Task<IReadOnlyList<ItadBundle>> GetGlobalBundlesPageAsync(
            int offset,
            int limit,
            CancellationToken cancellationToken,
            ItadRequestPriority priority = ItadRequestPriority.Background) =>
            throw new InvalidOperationException("a price pass never reads the global bundle catalog");

        public Task<string?> ResolveDealUrlAsync(
            string dealUrl,
            CancellationToken cancellationToken,
            ItadRequestPriority priority = ItadRequestPriority.Background) =>
            throw new InvalidOperationException("there is no ITAD deal link in this fixture");
    }

    /// <summary>gg.deals answers "this app id is not tracked", which is its authoritative no-match.</summary>
    private sealed class FakeGgDealsClient : IGgDealsClient
    {
        public int PriceCalls { get; private set; }

        public Task<IReadOnlyDictionary<int, GgDealsGamePrice>> GetPricesAsync(
            IReadOnlyCollection<int> steamAppIds, CancellationToken ct)
        {
            PriceCalls++;
            return Task.FromResult<IReadOnlyDictionary<int, GgDealsGamePrice>>(
                new Dictionary<int, GgDealsGamePrice>());
        }

        public Task<GgDealsBundlePage> GetActiveBundlesAsync(string? cursor, CancellationToken ct) =>
            throw new InvalidOperationException("a price pass never reads the gg.deals bundle catalog");
    }

    /// <summary>The one direct store the fixture makes due, answering under its production source key.</summary>
    private sealed class FakeEpicStore : IStorePriceProvider
    {
        public EpicAnswer Answer { get; set; } = EpicAnswer.Offer;

        public int ByTitleCalls { get; private set; }

        /// <summary>Scripted answer (a status, a timeout, a payload a parser cannot read, a cancellation).</summary>
        public Func<Task<StoreOffer?>>? Override { get; set; }

        public string Source => EpicStoreClient.StoreSource;

        public Task<StoreOffer?> FindOfferAsync(string title, string externalId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("this fixture has no stored store id");

        public Task<StoreOffer?> FindOfferByTitleAsync(string title, CancellationToken cancellationToken)
        {
            ByTitleCalls++;
            if (Override is not null)
            {
                return Override();
            }

            return Answer switch
            {
                EpicAnswer.Offer => Task.FromResult<StoreOffer?>(new StoreOffer(
                    EpicStoreClient.StoreSource,
                    "floppy-knights-6a735a",
                    "floppy-offer-id",
                    "Epic Games Store",
                    title,
                    16199,
                    16199,
                    "MXN",
                    0,
                    "https://store.epicgames.com/es-mx/p/floppy-knights-6a735a")),
                EpicAnswer.NoMatch => Task.FromResult<StoreOffer?>(null),
                _ => Task.FromException<StoreOffer?>(new HttpRequestException("store unavailable"))
            };
        }
    }

    /// <summary>Counts how many outbound calls a client that must never run actually received.</summary>
    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        public int HttpCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpCalls++;
            throw new InvalidOperationException("this store is not due in this scenario");
        }
    }

    /// <summary>Counts the saves the pipeline performs: "persisted" vs "did not even start a transaction".</summary>
    private sealed class SaveCounter : SaveChangesInterceptor
    {
        /// <summary>Saves performed since the last <see cref="Reset"/>.</summary>
        public int During { get; private set; }

        public void Reset() => During = 0;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            During++;
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            During++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class NullFxRateService : IFxRateService
    {
        public Task<FxRate?> GetRateAsync(
            string baseCurrency, string quoteCurrency, CancellationToken cancellationToken) =>
            Task.FromResult<FxRate?>(null);

        public Task<FxRate?> GetLatestRateAsync(
            string baseCurrency, string quoteCurrency, CancellationToken cancellationToken) =>
            Task.FromResult<FxRate?>(null);
    }

    /// <summary>
    /// The canonical-game backfill the batch's Steam write performs: a real row through the real repository.
    /// Only the batch shape reaches it, and it resolves by exact title so no merge rule is invented here.
    /// </summary>
    private sealed class RepositoryIdentityResolver(IRepository repository) : IGameIdentityResolver
    {
        public async Task<Game> ResolveOrCreateGameAsync(
            GameIdentityRequest request, CancellationToken cancellationToken = default)
        {
            var existing = await repository.Get<Game>()
                .FirstOrDefaultAsync(game => game.Title == request.Title, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            return await repository.Save(new Game
            {
                Title = request.Title,
                NormalizedTitle = request.Title.ToUpperInvariant()
            });
        }
    }

    private sealed class ThrowingWishlistClient : ISteamWishlistClient
    {
        // The price pass never reads the wishlist list: the rows are already persisted.
        public Task<SteamWishlistResult> GetWishlistAsync(string steamId64, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the price pass never fetches the wishlist list");
    }

    private sealed class OfflineUser : ICurrentUserService
    {
        public int? GetUserId() => null;

        public int GetRequiredUserId() => throw new InvalidOperationException("checks have no request user");

        public int? GetSessionVersion() => null;

        public Guid? GetSessionId() => null;

        public string GetName() => "provider-refresh-checks";

        public string GetEmail() => "provider-refresh-checks@example.test";

        public bool IsAdmin() => false;
    }
}
