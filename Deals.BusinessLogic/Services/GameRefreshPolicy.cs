namespace Deals.BusinessLogic.Services;

/// <summary>
/// How much of a game's price snapshot a refresh may touch.
///
/// <list type="bullet">
/// <item><see cref="SteamOnly"/>: the Steam store snapshot only, gated by the Steam cache window.</item>
/// <item><see cref="Full"/>: everything, forced. The operator's "refresh now" on one game.</item>
/// <item><see cref="BackgroundProviders"/>: the due external providers of an existing snapshot, gated by the
/// configured provider TTL (7 days by default). Never requests Steam.</item>
/// <item><see cref="StalePriceBatch"/>: the manual pending-price pass. The same phases as
/// <see cref="BackgroundProviders"/> <em>plus</em> the Steam direct snapshot, all gated by the monitor's
/// 72-hour window instead of the configured TTL, and nothing is ever forced. Steam belongs here because the
/// user asked for pending prices, not pending external prices: a five-day-old Steam price next to fresh
/// provider stamps is a pending price. The distinct member is deliberate — reusing
/// <see cref="BackgroundProviders"/> with a 7-day TTL is exactly the mistake this pass must not make, so the
/// mode itself refuses a longer TTL (<see cref="GameRefreshPolicy.MaximumStalePriceBatchTtl"/>).</item>
/// </list>
/// </summary>
public enum GameRefreshMode { SteamOnly, Full, BackgroundProviders, StalePriceBatch }

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
    /// <summary>
    /// The provider gate of <see cref="GameRefreshMode.StalePriceBatch"/>: the same 72 hours the freshness
    /// monitor buckets with, so "the panel says it is stale" and "the batch refreshes it" mean one thing.
    /// </summary>
    public const int StalePriceBatchHours = 72;

    /// <summary>The longest provider TTL a batch run may use. Anything longer is silently a 7-day pass.</summary>
    public static TimeSpan MaximumStalePriceBatchTtl { get; } = TimeSpan.FromHours(StalePriceBatchHours);

    /// <param name="providerTtl">
    /// Age at which a provider phase becomes due. For <see cref="GameRefreshMode.StalePriceBatch"/> it must not
    /// exceed <see cref="MaximumStalePriceBatchTtl"/>: the batch exists to reprice what the 72-hour monitor
    /// calls stale, so inheriting a longer configured TTL would make the job report work it never did.
    /// </param>
    public static GameRefreshPlan Create(GameRefreshMode mode, DateTime now, TimeSpan providerTtl,
        bool hasSnapshot, bool holdsDetails, DateTime observedAt, bool comparable,
        DateTime? itadAt, DateTime? ggAt, DateTime? epicAt, DateTime? microsoftAt,
        bool hasEpicIdentity, bool hasMicrosoftIdentity)
    {
        if (mode == GameRefreshMode.StalePriceBatch && providerTtl > MaximumStalePriceBatchTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerTtl),
                providerTtl,
                $"El modo de lote de precios solo admite una antigüedad máxima de {StalePriceBatchHours} h " +
                "(no reutiliza la ventana de proveedores configurada).");
        }

        // The background mode reads a persisted snapshot and never requests Steam: it is a provider price pass
        // over known games, not a detail fetch. The batch additionally owns the Steam phase, so it may run on a
        // snapshot that does not hold complete details yet — fetching Steam is precisely what repairs it.
        var batch = mode == GameRefreshMode.StalePriceBatch;
        var canRun = mode switch
        {
            GameRefreshMode.BackgroundProviders => hasSnapshot && holdsDetails,
            GameRefreshMode.StalePriceBatch => hasSnapshot,
            _ => true
        };

        // Steam's own cache window: 1 h for an interactive detail read, the 72-hour batch window for the price
        // pass. `Full` still forces it and the background mode still never asks for it.
        var steamTtl = batch ? providerTtl : TimeSpan.FromHours(1);
        var steam = canRun && mode != GameRefreshMode.BackgroundProviders &&
            (mode == GameRefreshMode.Full || !hasSnapshot ||
                !holdsDetails || observedAt == default || observedAt < now - steamTtl);

        // A stamp in the future is corrupt evidence. The batch treats it as due so the game is retried instead
        // of being selected by the SQL gate and doing nothing forever; every other mode keeps its old rule.
        bool Due(DateTime? stamp) => stamp is null || stamp < now - providerTtl || (batch && stamp > now);
        var external = canRun && mode != GameRefreshMode.SteamOnly;
        // Both non-interactive price modes keep the rule that a due direct-store phase needs trustworthy ITAD
        // links first, so an empty link set is never read as "the store does not sell it".
        var priceModes = mode is GameRefreshMode.BackgroundProviders or GameRefreshMode.StalePriceBatch;
        var force = mode == GameRefreshMode.Full;
        var itad = external && (force || Due(itadAt));
        var gg = external && (force || Due(ggAt));
        var epic = external && (force || Due(epicAt));
        var microsoft = external && (force || Due(microsoftAt));
        // Fresh ITAD prices need no replacement. Missing direct identity still needs trustworthy
        // ITAD links before a due direct-store phase may interpret an empty link as "no match".
        var identity = priceModes && comparable &&
            ((epic && !hasEpicIdentity) || (microsoft && !hasMicrosoftIdentity));
        return new(canRun, steam, itad, itad || identity, gg, epic, microsoft);
    }
}
