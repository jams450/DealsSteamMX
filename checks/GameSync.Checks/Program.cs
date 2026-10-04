using Deals.BusinessLogic.Services;

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
Console.WriteLine("PASS: staged refresh policy checks");
