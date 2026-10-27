using Deals.BusinessLogic.Services;

ExternalUrlChecks.Run();
await GovernorChecks.RunAsync();

var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
var fresh = now;
var stale = now.AddDays(-8);
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
}
GameRefreshPlan Plan(GameRefreshMode mode, DateTime? itad, DateTime? gg, DateTime? epic,
    DateTime? ms, bool epicIdentity = true, bool msIdentity = true, bool snapshot = true) =>
    GameRefreshPolicy.Create(mode, now, TimeSpan.FromDays(7), snapshot, true, fresh,
        true, itad, gg, epic, ms, epicIdentity, msIdentity);

Check(Plan(GameRefreshMode.BackgroundProviders, fresh, fresh, fresh, fresh).Steps.Count == 0, "fresh skips all");
var all = Plan(GameRefreshMode.BackgroundProviders, stale, stale, stale, stale);
Check(!all.NeedsSteam && all.Steps.SequenceEqual(new[] { "itad", "ggdeals", "epic", "microsoft" }), "Steam-free ordered providers");
Check(Plan(GameRefreshMode.SteamOnly, stale, stale, stale, stale).Steps.Count == 0, "GET Steam only");
Check(Plan(GameRefreshMode.Full, fresh, fresh, fresh, fresh).NeedsSteam, "force refresh Steam");
Check(Plan(GameRefreshMode.Full, fresh, fresh, fresh, fresh).Steps.SequenceEqual(all.Steps), "Full forces every fresh provider");
Check(!Plan(GameRefreshMode.BackgroundProviders, stale, stale, stale, stale, snapshot: false).CanRun, "absent snapshot rejected");
var identity = Plan(GameRefreshMode.BackgroundProviders, fresh, fresh, stale, fresh, epicIdentity: false);
Check(identity.FetchItad && !identity.RefreshItadPrices && identity.RefreshEpic && !identity.RefreshMicrosoft,
    "identity prerequisite must not refresh fresh prices");
Check(!Plan(GameRefreshMode.BackgroundProviders, fresh, fresh, fresh, fresh, false, false).FetchItad,
    "missing identity alone does not trigger ITAD");
Check(Plan(GameRefreshMode.BackgroundProviders, fresh, stale, fresh, fresh).Steps.SequenceEqual(new[] { "ggdeals" }), "independent gg gate");
Check(Plan(GameRefreshMode.BackgroundProviders, stale, fresh, fresh, fresh).Steps.SequenceEqual(new[] { "itad" }), "independent ITAD gate");
Check(Plan(GameRefreshMode.BackgroundProviders, fresh, fresh, fresh, stale).Steps.SequenceEqual(new[] { "microsoft" }), "independent MS gate");
var expiredSteam = GameRefreshPolicy.Create(GameRefreshMode.SteamOnly, now, TimeSpan.FromDays(7),
    true, true, now.AddHours(-2), true, fresh, fresh, fresh, fresh, true, true);
Check(expiredSteam.NeedsSteam, "Steam 1h TTL");
Check(!Plan(GameRefreshMode.SteamOnly, fresh, fresh, fresh, fresh).NeedsSteam, "fresh Steam cache");
var noncomparableFull = GameRefreshPolicy.Create(GameRefreshMode.Full, now, TimeSpan.FromDays(7),
    true, true, fresh, false, fresh, fresh, fresh, fresh, true, true);
Check(noncomparableFull.Steps.SequenceEqual(all.Steps), "Full noncomparable clears snapshots via no-match");
// --- The batch window (catalog price monitor, spec S5/S6 → spec S2/S3 of the configurable pass) ----------
// The pending-price pass must gate providers with the window the requester chose, never with the configured
// 7-day TTL: reusing the TTL would make the job skip exactly the rows the job was asked to reprice. 72 h is the
// documented DEFAULT of that window and the monitor's fixed window, not a ceiling: the bounded explicit choice is
// 1..720 h. These assertions are the reason the mode exists, so they are stated against both modes with the same
// stamps.
const int batchHours = GameRefreshPolicy.StalePriceBatchHours;
Check(batchHours == 72, "72 h is the documented default window of the batch");
Check(GameRefreshPolicy.MinimumStalePriceBatchTtl == TimeSpan.FromHours(1),
    "the batch accepts a chosen window from 1 h");
Check(GameRefreshPolicy.MaximumStalePriceBatchTtl == TimeSpan.FromHours(720),
    "the batch accepts a chosen window up to 720 h");

var fiveDaysAgo = now.AddDays(-5);
GameRefreshPlan Batch(DateTime? itad, DateTime? gg, DateTime? epic, DateTime? ms,
        bool epicIdentity = true, bool msIdentity = true, bool snapshot = true) =>
    GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromHours(batchHours),
        snapshot, true, fresh, true, itad, gg, epic, ms, epicIdentity, msIdentity);

var batchStale = Batch(fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo);
Check(batchStale.CanRun, "the batch runs on a stored snapshot");
Check(!batchStale.NeedsSteam, "the batch never requests Steam details");
Check(batchStale.Steps.SequenceEqual(new[] { "itad", "ggdeals", "epic", "microsoft" }),
    "a five-day-old provider is due for the 72h batch");
Check(Plan(GameRefreshMode.BackgroundProviders, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo).Steps.Count == 0,
    "the same five-day-old provider stays fresh under the configured 7-day TTL");
Check(!Batch(now.AddDays(-2), now.AddDays(-2), now.AddDays(-2), now.AddDays(-2)).Steps.Any(),
    "a provider consulted two days ago stays skipped by the batch");
Check(Batch(null, null, null, null).Steps.Count == 4, "missing stamps are due in the batch");
Check(Batch(now.AddDays(-1), null, now.AddDays(-1), now.AddDays(-1)).Steps.SequenceEqual(new[] { "ggdeals" }),
    "the batch gates every provider independently");
Check(!Batch(fiveDaysAgo, fresh, fiveDaysAgo, fresh).NeedsSteam, "the batch never forces a Steam refresh");
Check(!Batch(fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo).RefreshItadPrices == false,
    "a stale ITAD snapshot is repriced by the batch");
Check(!Batch(fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, snapshot: false).CanRun,
    "the batch refuses a game with no snapshot");
var batchIdentity = Batch(fresh, fresh, fresh, fiveDaysAgo, msIdentity: false);
Check(batchIdentity.FetchItad && !batchIdentity.RefreshItadPrices && batchIdentity.RefreshMicrosoft,
    "the batch keeps the identity-before-direct-store rule");

// The window is a bounded CHOICE (spec S2/S3: 1..720 h). 7 days = 168 h is a legitimate choice now, not an
// inherited TTL; what the guard refuses is anything outside the documented bounds. The batch still never reads the
// configured provider TTL itself — it only ever receives the explicit window.
GameRefreshPlan Windowed(int hours) =>
    GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromHours(hours),
        true, true, fresh, true, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, true, true);

Check(Windowed(24).Steps.Count == 4, "a 24 h chosen window makes a five-day-old provider due");
Check(Windowed(72).Steps.Count == 4, "a 72 h chosen window makes a five-day-old provider due");
Check(Windowed(168).Steps.Count == 0, "a 168 h chosen window keeps the same provider fresh");
Check(Windowed(720).Steps.Count == 0, "720 h is the widest window and it is accepted");
Check(Windowed(1).Steps.Count == 4, "1 h is the narrowest window and it is accepted");

bool Rejects(TimeSpan ttl)
{
    try
    {
        GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, ttl,
            true, true, fresh, true, fresh, fresh, fresh, fresh, true, true);
        return false;
    }
    catch (ArgumentOutOfRangeException)
    {
        return true;
    }
}

Check(Rejects(TimeSpan.FromHours(721)), "the batch refuses a window above 720 h");
Check(Rejects(TimeSpan.FromMinutes(30)), "the batch refuses a window below 1 h");

// The same Steam snapshot is inside or outside the window depending on the chosen hours, which is the whole point
// of propagating the choice instead of keeping a hidden 72 h guard per game.
GameRefreshPlan SteamWindow(int hours) =>
    GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromHours(hours),
        true, true, now.AddHours(-100), true, fresh, fresh, fresh, fresh, true, true);
Check(SteamWindow(24).NeedsSteam, "a 100 h old Steam price is due for a 24 h window");
Check(SteamWindow(72).NeedsSteam, "a 100 h old Steam price is due for a 72 h window");
Check(!SteamWindow(168).NeedsSteam, "the same Steam price is already inside a 168 h window");

// The Steam direct snapshot is a price too: a game whose Steam price is five days old and whose provider
// stamps are all fresh must still be repriced by the batch, or "pending prices" would silently mean "external
// prices only" and the user's 72h catalog could never be cleared.
var freshStamps = (DateTime?)now.AddHours(-1);
var steamOnly = GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromHours(72),
    true, true, now.AddDays(-5), true, freshStamps, freshStamps, freshStamps, freshStamps, true, true);
Check(steamOnly.NeedsSteam, "the batch reprices a Steam direct price older than 72 h");
Check(steamOnly.Steps.Count == 0, "and does not force the providers that are still fresh");

var steamFresh = GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromHours(72),
    true, true, now.AddHours(-2), true, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, fiveDaysAgo, true, true);
Check(!steamFresh.NeedsSteam, "a Steam price inside the window is not re-requested");
Check(steamFresh.Steps.Count == 4, "while the stale providers still are");

// The Steam phase is a 72 h gate, not a force: Full still forces it, SteamOnly keeps its 1 h cache, and the
// background mode never requests Steam at all.
Check(GameRefreshPolicy.Create(GameRefreshMode.Full, now, TimeSpan.FromDays(7),
    true, true, now.AddMinutes(-5), true, fresh, fresh, fresh, fresh, true, true).NeedsSteam,
    "Full still forces Steam");
Check(!GameRefreshPolicy.Create(GameRefreshMode.BackgroundProviders, now, TimeSpan.FromDays(7),
    true, true, now.AddDays(-30), true, stale, stale, stale, stale, true, true).NeedsSteam,
    "the background mode still never requests Steam");
Check(!GameRefreshPolicy.Create(GameRefreshMode.SteamOnly, now, TimeSpan.FromDays(7),
    true, true, now.AddMinutes(-30), true, fresh, fresh, fresh, fresh, true, true).NeedsSteam,
    "SteamOnly keeps its 1 h cache window");

// An incomplete (legacy) snapshot can still be repaired by the Steam phase, and a game with no snapshot at
// all is refused rather than attempted.
var incomplete = GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromHours(72),
    true, false, now.AddDays(-10), true, freshStamps, freshStamps, freshStamps, freshStamps, true, true);
Check(incomplete.CanRun, "an incomplete snapshot is still repairable");
Check(incomplete.NeedsSteam, "because Steam details are what it is missing");
Check(!GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromHours(72),
    false, false, default, true, freshStamps, freshStamps, freshStamps, freshStamps, true, true).CanRun,
    "a game with no snapshot is refused");

// A future consultation stamp is corrupt evidence. The batch treats it as due (otherwise the game would be
// selected by the SQL gate and then do nothing, forever, because the policy would never consider it due).
Check(GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromHours(72),
    true, true, now.AddHours(-1), true, now.AddHours(3), fresh, fresh, fresh, true, true).FetchItad,
    "the batch treats a future stamp as due");
Check(!Plan(GameRefreshMode.BackgroundProviders, now.AddHours(3), fresh, fresh, fresh).FetchItad,
    "the background mode keeps ignoring a future stamp exactly as before");

Console.WriteLine("PASS: staged refresh policy checks");
