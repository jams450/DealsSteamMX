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
// --- The 72-hour batch gate (catalog price monitor, spec S5/S6) ------------------------------------------
// The pending-price pass must gate providers with the monitor's 72 hours, never with the configured 7-day
// TTL: reusing the TTL would make the job skip exactly the rows the panel calls stale. These assertions are
// the reason the mode exists, so they are stated against both modes with the same stamps.
const int batchHours = GameRefreshPolicy.StalePriceBatchHours;
Check(batchHours == 72, "the batch window is the monitor's 72 hours");
Check(GameRefreshPolicy.MaximumStalePriceBatchTtl == TimeSpan.FromHours(72),
    "the batch never accepts a longer provider TTL");

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

var rejectedSevenDays = false;
try
{
    GameRefreshPolicy.Create(GameRefreshMode.StalePriceBatch, now, TimeSpan.FromDays(7),
        true, true, fresh, true, fresh, fresh, fresh, fresh, true, true);
}
catch (ArgumentOutOfRangeException)
{
    rejectedSevenDays = true;
}

Check(rejectedSevenDays, "the batch refuses to inherit the 7-day provider TTL");

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
