namespace Deals.BusinessLogic.Services;

public enum GameRefreshMode { SteamOnly, Full, BackgroundProviders }

public sealed record GameRefreshPlan(bool CanRun, bool NeedsSteam, bool RefreshItadPrices,
    bool FetchItad, bool RefreshGg, bool RefreshEpic, bool RefreshMicrosoft)
{
    public IReadOnlyList<string> Steps => new[]
    {
        FetchItad ? "itad" : null, RefreshGg ? "ggdeals" : null,
        RefreshEpic ? "epic" : null, RefreshMicrosoft ? "microsoft" : null
    }.OfType<string>().ToArray();
}

public static class GameRefreshPolicy
{
    public static GameRefreshPlan Create(GameRefreshMode mode, DateTime now, TimeSpan providerTtl,
        bool hasSnapshot, bool holdsDetails, DateTime observedAt, bool comparable,
        DateTime? itadAt, DateTime? ggAt, DateTime? epicAt, DateTime? microsoftAt,
        bool hasEpicIdentity, bool hasMicrosoftIdentity)
    {
        var background = mode == GameRefreshMode.BackgroundProviders;
        var canRun = !background || (hasSnapshot && holdsDetails);
        var steam = !background && (mode == GameRefreshMode.Full || !hasSnapshot ||
            !holdsDetails || observedAt == default || observedAt < now.AddHours(-1));
        bool Due(DateTime? stamp) => stamp is null || stamp < now - providerTtl;
        var external = canRun && mode != GameRefreshMode.SteamOnly;
        var force = mode == GameRefreshMode.Full;
        var itad = external && (force || Due(itadAt));
        var gg = external && (force || Due(ggAt));
        var epic = external && (force || Due(epicAt));
        var microsoft = external && (force || Due(microsoftAt));
        // Fresh ITAD prices need no replacement. Missing direct identity still needs trustworthy
        // ITAD links before a due direct-store phase may interpret an empty link as "no match".
        var identity = background && comparable && ((epic && !hasEpicIdentity) || (microsoft && !hasMicrosoftIdentity));
        return new(canRun, steam, itad, itad || identity, gg, epic, microsoft);
    }
}
