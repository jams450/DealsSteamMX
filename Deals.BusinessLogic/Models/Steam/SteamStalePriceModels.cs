namespace Deals.BusinessLogic.Models.Steam;

/// <summary>
/// Names of the price phases the manual pending-price pass may run, in the order the pipeline runs them.
/// </summary>
public static class SteamStalePricePhases
{
    public const string Steam = "steam";

    public const string Itad = "itad";

    public const string GgDeals = "ggdeals";

    public const string Epic = "epic";

    public const string Microsoft = "microsoft";
}

/// <summary>
/// What one stale-price attempt actually did (spec S5/S6). It exists because the refresh pipeline absorbs a
/// provider fault into a <c>Failed</c> phase result and still returns details: without this typed outcome the
/// caller cannot tell "the store answered and had nothing comparable" from "the store did not answer", and a
/// failed call would be counted as a completed one.
///
/// <para>
/// <see cref="DuePhases"/> is what the 72-hour gates asked for (including the identity-only ITAD read that must
/// precede a direct-store phase); <see cref="AdvancedPhases"/> is what really persisted an authoritative
/// answer, proven by its own timestamp advancing; <see cref="DegradedPhases"/> is what was due and failed. A
/// phase can be due, fail, and still leave a usable price behind — the attempt is still degraded.
/// </para>
/// </summary>
public sealed record SteamStalePriceRefresh(
    bool Ran,
    bool SteamDue,
    IReadOnlyList<string> DuePhases,
    IReadOnlyList<string> AdvancedPhases,
    IReadOnlyList<string> DegradedPhases)
{
    /// <summary>At least one due phase failed: the attempt is not a clean result.</summary>
    public bool Degraded => DegradedPhases.Count > 0;

    /// <summary>At least one due phase persisted an authoritative answer.</summary>
    public bool AnyAdvanced => AdvancedPhases.Count > 0;

    /// <summary>
    /// The per-game gate declined: there is no snapshot to work from (the row was removed between selection and
    /// refresh). Not a failure — there was nothing to refresh.
    /// </summary>
    public static SteamStalePriceRefresh Declined() => new(false, false, [], [], []);

    /// <summary>
    /// Did a due provider phase really persist an authoritative answer?
    ///
    /// An authoritative answer — prices or an explicit no-match — always writes its own timestamp, so a phase
    /// counts only when it did not fail <em>and</em> its stamp moved. The second half matters: a phase that
    /// reports success without touching its timestamp is degradation (an unknown write path), never an
    /// authoritative "nothing comparable". A failed phase is never an answer, even if some stamp moved.
    /// </summary>
    public static bool PersistedAnswer(bool failed, DateTime? before, DateTime? after) =>
        !failed && after is not null && after != before;
}
