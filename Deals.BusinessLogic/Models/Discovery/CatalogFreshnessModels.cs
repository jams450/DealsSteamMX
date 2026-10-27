using System.Linq.Expressions;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>
/// The freshness window of the monitor (spec S4/S6). It is a constant, not a setting: the panel's contract
/// says "72 hours" and the wishlist badges keep saying it whatever a manual run was asked for. The notifier's
/// <c>MaxSnapshotAgeHours</c> is a *different* knob (it decides which snapshots may become alerts); reusing it
/// here would let a notification setting silently change what the monitor calls stale.
///
/// The manual pending-price pass does not use this constant as its window: it takes the requester's chosen
/// <c>ageHours</c> (spec S2/S3), which is bounded by <see cref="CatalogPriceRefreshLimits"/>. 72 is its default,
/// and it stays the monitor's only value on purpose — one surface is configurable, the other is not.
/// </summary>
public static class CatalogFreshness
{
    public const int Hours = 72;

    public static TimeSpan Window { get; } = TimeSpan.FromHours(Hours);

    public static DateTime Cutoff(DateTime asOfUtc) => asOfUtc - Window;

    /// <summary>
    /// Cutoff of a chosen window. Out-of-range hours are refused instead of being coerced to 72: a silent fallback
    /// would make the pass price a window nobody asked for, and the run record would claim the chosen one.
    /// </summary>
    public static DateTime Cutoff(DateTime asOfUtc, int ageHours) =>
        asOfUtc - TimeSpan.FromHours(CatalogPriceRefreshLimits.ValidateAgeHours(ageHours));
}

/// <summary>
/// The four status tokens of the monitor. `recent`, `stale` and `missing` are price *coverage* buckets, not
/// verdicts about a shop: a stale price is an old price that was observed, a missing game is one where no
/// usable comparable MXN price exists — which can mean "the game is not sold anywhere we can price", "no
/// identity match", or "no provider has answered yet". None of them claims a provider failed.
/// </summary>
public static class CatalogFreshnessStatus
{
    /// <summary>The filter that selects every game of the caller's wishlist. Not a game status.</summary>
    public const string All = "all";

    /// <summary>At least one usable comparable price observed inside the window.</summary>
    public const string Recent = "recent";

    /// <summary>The cheapest usable comparable price was observed before the window.</summary>
    public const string Stale = "stale";

    /// <summary>No usable comparable price at all, in any age.</summary>
    public const string Missing = "missing";

    /// <summary>Stable order of the filter tokens, used by the panel and by every count.</summary>
    public static readonly IReadOnlyList<string> Filterable = [All, Recent, Stale, Missing];

    /// <summary>The three game statuses; the filter additionally accepts <see cref="All"/>.</summary>
    public static readonly IReadOnlyList<string> GameStatuses = [Recent, Stale, Missing];

    public static bool TryParse(string? raw, out string status)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case All:
                status = All;
                return true;
            case Recent:
                status = Recent;
                return true;
            case Stale:
                status = Stale;
                return true;
            case Missing:
                status = Missing;
                return true;
            default:
                status = All;
                return false;
        }
    }

    /// <summary>
    /// Normalizes the query token, or throws the Spanish 400 the API already uses for a bad token. An absent
    /// token is the default filter (<c>all</c>), never a silent guess among the three buckets.
    /// </summary>
    public static string Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return All;
        }

        return TryParse(raw, out var status)
            ? status
            : throw new ArgumentException(
                $"Estado de actualización desconocido: '{raw}'. Valores válidos: all, recent, stale, missing.",
                nameof(raw));
    }

    /// <summary>
    /// The one place that turns an observation instant into a bucket. The instant must be the CHOSEN (cheapest)
    /// price's, never the newest of the game (see <see cref="CatalogFreshnessGameState"/>). A null instant is
    /// `missing`; a stamp in the future is not evidence of freshness and not evidence of staleness either, so it is
    /// `missing` too — the SQL side already refuses such rows, and this guard keeps the rule true for any caller.
    /// </summary>
    public static string Classify(DateTime? chosenObservedAtUtc, DateTime asOfUtc)
    {
        if (chosenObservedAtUtc is not { } observedAtUtc || observedAtUtc > asOfUtc)
        {
            return Missing;
        }

        return observedAtUtc >= CatalogFreshness.Cutoff(asOfUtc) ? Recent : Stale;
    }
}

/// <summary>
/// One usable comparable price the monitor reads: a provider row or the Steam direct snapshot, reduced to the
/// facts the monitor needs to pick one winner per game — which game, who priced it (provider, shop, pricing type),
/// the provider's stable key, the amount and when it was observed. Object initializer, like every projected shape
/// in this catalog: EF Core refuses a positional constructor inside a set operation, and the monitor's whole
/// price universe is one <c>UNION ALL</c>.
/// </summary>
public sealed record CatalogFreshnessPriceRow
{
    public long GameId { get; init; }

    /// <summary>
    /// The source of the price (<c>itad</c>, <c>ggdeals</c>, <c>epic</c>, <c>microsoft</c>, <c>steam</c>…).
    /// </summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// The provider's own key for this offer (the app id for a Steam snapshot). Read only to break a full tie
    /// between two rows of the same game, provider, amount and instant, so the winner never depends on row order.
    /// </summary>
    public string OfferKey { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="Provider"/> and <see cref="OfferKey"/> joined by a separator no provider name uses, so ONE
    /// <c>MIN</c> breaks both remaining ties (provider first, then the provider's own key) and the winner stays a
    /// deterministic row instead of depending on row order. Built in SQL, where it is also compared.
    /// </summary>
    public string StableKey { get; init; } = string.Empty;

    public string ShopName { get; init; } = string.Empty;

    public string PricingType { get; init; } = string.Empty;

    public int PriceMinor { get; init; }

    public DateTime ObservedAt { get; init; }
}

/// <summary>
/// One canonical game's coverage: the single price the panel shows and explains — the CHEAPEST usable comparable
/// MXN price, and among equal amounts the newest observation — plus the bucket that price's instant falls in.
///
/// The rule is deliberately not "the newest observation of any shop" (user correction S9, «El precio más barato
/// guardado»): a fresh and expensive Steam price must never hide an older, cheaper external offer, because then
/// the bucket, the date and the amount the user reads would describe a price that is not the cheapest one the
/// wishlist actually has. <see cref="ChosenObservedAt"/> is the date of THAT price, and <see cref="ChosenPriceMinor"/>,
/// <see cref="ChosenProvider"/>, <see cref="ChosenShopName"/> and <see cref="ChosenPricingType"/> all come from the
/// same row — price and label are never mixed.
/// </summary>
public sealed record CatalogFreshnessGameState
{
    public long GameId { get; init; }

    /// <summary>When the chosen (cheapest) price was observed; <c>null</c> only when the game has no usable price.</summary>
    public DateTime? ChosenObservedAt { get; init; }

    /// <summary>The chosen price in minor MXN units; <c>null</c> exactly when <see cref="ChosenObservedAt"/> is.</summary>
    public int? ChosenPriceMinor { get; init; }

    /// <summary>The source the chosen price came from (<c>itad</c>, <c>steam</c>, …).</summary>
    public string? ChosenProvider { get; init; }

    public string? ChosenShopName { get; init; }

    public string? ChosenPricingType { get; init; }

    public string Status { get; init; } = CatalogFreshnessStatus.Missing;
}

/// <summary>
/// The MX <c>steam_games</c> row of a game, reduced to its app id and its provider consultation stamps.
/// Each stamp is the instant a provider last answered authoritatively for this game, which is a different
/// fact from the price date: a provider can be consulted and have nothing comparable to sell.
/// </summary>
public sealed record CatalogFreshnessSteamRow
{
    public long GameId { get; init; }

    public int SteamGameId { get; init; }

    public int AppId { get; init; }

    public DateTime ObservedAt { get; init; }

    public DateTime? OffersRefreshedAt { get; init; }

    public DateTime? GgDealsRefreshedAt { get; init; }

    public DateTime? EpicRefreshedAt { get; init; }

    public DateTime? MicrosoftRefreshedAt { get; init; }
}

/// <summary>
/// One provider of one game: the newest price it reported and when it was last consulted. Both are nullable
/// and mean "unknown" when null — never "never attempted" and never "failed": persistence cannot prove
/// either, so the monitor does not claim them.
/// </summary>
public sealed record CatalogFreshnessProviderDate(
    string Provider,
    DateTime? PriceObservedAtUtc,
    DateTime? LastCheckedAtUtc);

/// <summary>
/// One row of the monitor: a canonical game (Steam-less ones included), its coverage bucket, the chosen (cheapest)
/// price that bucket was derived from — amount, shop, source and pricing type, all from that one row — and the
/// per-provider dates that reveal partial staleness.
///
/// The additive <c>chosen*</c> fields are the frozen contract the discover cards render (user correction S9), so the
/// panel never has to re-derive which price a bucket came from; <see cref="PriceObservedAtUtc"/> is the same
/// winner's instant. All of them are <c>null</c> exactly when the game has no usable comparable MXN price.
/// </summary>
public sealed record CatalogFreshnessItem(
    long GameId,
    string Name,
    string? ImageUrl,
    int? SteamAppId,
    string Status,
    DateTime? PriceObservedAtUtc,
    int? ChosenPriceMinor,
    string? ChosenShopName,
    string? ChosenSource,
    string? ChosenPricingType,
    IReadOnlyList<CatalogFreshnessProviderDate> Providers);

/// <summary>
/// The four counters of the requester's wishlist. <see cref="Total"/> is every canonical game on that list,
/// whatever the filter is — not the catalog: this monitor only describes the list the user asked about
/// (spec S4, user correction «es solo whislist»).
/// </summary>
public sealed record CatalogFreshnessCounts(int Total, int Recent, int Stale, int Missing)
{
    /// <summary>Folds the grouped SQL result into the envelope, filling the buckets with zero when absent.</summary>
    public static CatalogFreshnessCounts Of(IEnumerable<CatalogFreshnessCount> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var rows = counts.ToList();
        int Of7(string status) => rows
            .Where(row => string.Equals(row.Status, status, StringComparison.Ordinal))
            .Sum(row => row.Total);

        return new CatalogFreshnessCounts(
            rows.Sum(row => row.Total),
            Of7(CatalogFreshnessStatus.Recent),
            Of7(CatalogFreshnessStatus.Stale),
            Of7(CatalogFreshnessStatus.Missing));
    }
}

/// <summary>One row of the grouped counters, as SQL produces it.</summary>
public sealed record CatalogFreshnessCount
{
    public string Status { get; init; } = string.Empty;

    public int Total { get; init; }
}

/// <summary>
/// One page of the monitor plus the counters of the requester's whole wishlist, so the panel paints every
/// filter badge with one request. <see cref="Total"/> is the selected filter's total; <see cref="Counts"/> is
/// never narrowed by it.
///
/// <see cref="UnresolvedWishlistEntries"/> is not a bucket and is not part of <see cref="Counts"/>: it counts
/// wishlist <em>entries</em> (rows) that no exact identity path could attribute to an existing game. They are
/// outside every canonical counter on purpose — nothing is invented for them — but they are never zeroed away
/// either, so a wishlist the monitor cannot read still says so.
/// </summary>
public sealed record CatalogFreshnessPage(
    DateTime AsOfUtc,
    int FreshnessHours,
    int Page,
    int PageSize,
    int Total,
    CatalogFreshnessCounts Counts,
    int UnresolvedWishlistEntries,
    IReadOnlyList<CatalogFreshnessItem> Items);

/// <summary>
/// One game the manual pending-price pass may refresh, reduced to what the provider pipeline needs: the
/// canonical game, the Steam app id of its deterministic MX snapshot, and the price instant that made it
/// pending (<c>null</c> when the game has no usable price at all).
/// </summary>
public sealed record CatalogPriceRefreshCandidate
{
    public long GameId { get; init; }

    public int AppId { get; init; }

    /// <summary>The chosen (cheapest) amount before the attempt, so the pass measures the same winner afterwards.</summary>
    public int? ChosenPriceMinor { get; init; }

    /// <summary>The chosen (cheapest) price instant before the attempt.</summary>
    public DateTime? ChosenObservedAt { get; init; }
}

/// <summary>
/// Hard bounds of the manual pending-price pass. The batch is a constant on purpose — an option would let an
/// operator ask for the unbounded sweep this job exists to avoid — and so is the range of the one option the pass
/// does take: how many hours of price age the requester wants re-priced (spec S2/S3).
///
/// <see cref="DefaultAgeHours"/> is the documented default, not a ceiling: it is the same 72 h the monitor uses, but
/// the pass accepts any whole number of hours from <see cref="MinAgeHours"/> to <see cref="MaxAgeHours"/>. The
/// monitor keeps its fixed window; only this job is parameterised.
/// </summary>
public static class CatalogPriceRefreshLimits
{
    public const int BatchSize = 20;

    /// <summary>The narrowest meaningful window: anything below an hour is the interactive refresh's business.</summary>
    public const int MinAgeHours = 1;

    /// <summary>Thirty days. Wide enough to re-price an abandoned wishlist, still bounded.</summary>
    public const int MaxAgeHours = 720;

    /// <summary>The window a run gets when the requester does not choose one (spec S1/S2).</summary>
    public const int DefaultAgeHours = 72;

    /// <summary>
    /// The single validation of a chosen window, shared by the option parser, the query cutoff, the pass and the
    /// per-game provider gate. It returns the value so callers can validate and use it in one expression; an
    /// out-of-range window throws instead of falling back to the default, because a silent fallback would reprice a
    /// window nobody asked for and record the asked one.
    /// </summary>
    public static int ValidateAgeHours(int ageHours, string? paramName = null)
    {
        if (ageHours < MinAgeHours || ageHours > MaxAgeHours)
        {
            throw new ArgumentOutOfRangeException(
                paramName ?? nameof(ageHours),
                ageHours,
                $"La antigüedad mínima debe estar entre {MinAgeHours} y {MaxAgeHours} horas.");
        }

        return ageHours;
    }
}

/// <summary>
/// What one refresh attempt turned out to be (spec S5/S6). It is a pure decision over the typed phase outcome
/// and the game's own price instant, so the honest accounting can be checked offline instead of only through a
/// database.
/// </summary>
public enum CatalogPriceRefreshVerdict
{
    /// <summary>A due phase persisted an authoritative answer and the game's chosen (cheapest) price moved.</summary>
    Updated,

    /// <summary>Nothing changed: the gate declined, or the answers were identical to what was stored.</summary>
    Skipped,

    /// <summary>Every due phase answered and the game still has no usable comparable MXN price.</summary>
    NoComparablePrice,

    /// <summary>At least one due phase failed, whatever else succeeded.</summary>
    Degraded,
}

/// <summary>
/// The one place that turns a <see cref="SteamStalePriceRefresh"/> plus the game's price instant into a
/// counter. Degradation wins over a partial success on purpose: a pass that wrote one price and failed another
/// phase is not a clean update, and reporting it as one is exactly the false claim this job must avoid.
/// </summary>
public static class CatalogPriceRefreshClassification
{
    public static CatalogPriceRefreshVerdict Classify(
        SteamStalePriceRefresh attempt,
        bool hasUsablePriceAfter,
        bool advanced)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (attempt.Degraded)
        {
            return CatalogPriceRefreshVerdict.Degraded;
        }

        if (!attempt.Ran)
        {
            return CatalogPriceRefreshVerdict.Skipped;
        }

        if (advanced)
        {
            return CatalogPriceRefreshVerdict.Updated;
        }

        // An authoritative answer that left no usable price is a no-match, not a failure: the stores were
        // consulted and had nothing comparable in this market.
        return hasUsablePriceAfter
            ? CatalogPriceRefreshVerdict.Skipped
            : CatalogPriceRefreshVerdict.NoComparablePrice;
    }
}

/// <summary>
/// The pass's own "did the price move" measurement (spec S9, user correction «El precio más barato guardado»).
///
/// It compares the CHOSEN winner of the monitor — the cheapest usable comparable price, with its amount and
/// instant — before and after the attempt, so an attempt that only renewed a more expensive snapshot is a skip and
/// not a price update. Both honest movements count: the same amount observed again at a new instant (a real
/// re-observation of the price the panel shows) and a different best amount (the cheapest price changed).
///
/// The shop and source labels are deliberately not part of the comparison: a winner row is identified by its amount
/// and instant, so the labels cannot move without one of those two moving — except when a brand-new offer appears at
/// the very same amount and instant of another provider, which changes a label and no price. That case stays a skip
/// on purpose, because the price the panel shows did not change.
///
/// A game with no chosen price before and none after is not an update; that is the no-comparable-price answer and
/// is classified by <see cref="CatalogPriceRefreshClassification"/>.
/// </summary>
public static class CatalogPriceRefreshMeasurement
{
    public static bool Advanced(
        int? beforePriceMinor,
        DateTime? beforeObservedAt,
        int? afterPriceMinor,
        DateTime? afterObservedAt) =>
        afterObservedAt is not null &&
        (beforeObservedAt is null || beforePriceMinor != afterPriceMinor || beforeObservedAt != afterObservedAt);
}

/// <summary>
/// Counters of one manual pending-price pass (spec S5). Every field is a different fact, and the ones that
/// are easy to conflate are kept apart on purpose:
/// <list type="bullet">
/// <item><b>selected</b>: games this batch actually attempted (at most
/// <see cref="CatalogPriceRefreshLimits.BatchSize"/>);</item>
/// <item><b>updated</b>: selected games whose chosen (cheapest) price instant or amount really moved, i.e. a
/// provider answer changed the price the panel shows. Nothing is counted here because a gate ran, because a
/// request was sent, or because a more expensive snapshot was renewed;</item>
/// <item><b>skipped</b>: selected games that already had a usable price and whose chosen price did not move —
/// fresh phases, or a provider answer that changed nothing (a renewed but more expensive price included);</item>
/// <item><b>gateDeclined</b>: selected games whose per-game gate declined because the snapshot disappeared
/// between selection and refresh. Kept apart from a real skip: nothing was asked, so nothing was answered;</item>
/// <item><b>noComparablePrice</b>: selected games that ended with no usable comparable MXN price at all (the
/// stores answered, and had nothing to sell in this market);</item>
/// <item><b>unsupported</b>: pending games this pass cannot price at all because they have no MX Steam
/// identity. They keep their coverage bucket — nothing is cleared and nothing is pretended;</item>
/// <item><b>notDue</b>: pending games excluded because every provider gate is still fresh (asked inside the
/// window, nothing comparable came back). This is what stops an unpriceable title from taking a slot on every
/// run;</item>
/// <item><b>degraded</b>: selected games where a due phase answered <em>failed</em>. The existing refresh
/// pipeline absorbs those faults and still returns details, so this counter (fed by
/// <see cref="SteamStalePriceRefresh.DegradedPhases"/>) is what keeps them from being counted as updates. A
/// failed answer does not advance a stamp, so those games stay due and are retried;</item>
/// <item><b>errors</b>: exceptions that escaped the refresh entirely;</item>
/// <item><b>remaining</b>: pending games recomputed after the pass WITH THE SAME CHOSEN WINDOW, unsupported and
/// cooled-down ones included. Measuring it with another window would make a run look finished that only moved the
/// boundary it was measured against;</item>
/// <item><b>unresolvedWishlistEntries</b>: wishlist <em>entries</em> (rows, not games) that no exact identity
/// path could attribute to an existing game. They are not pending games — there is nothing to price — but they
/// are not coverage either, so the pass reports them and never calls a zero-selected run "nothing pending" while
/// they exist.</item>
/// <item><b>ageHours</b>: the window this run really used (spec S2/S3). It travels in the record so a history entry
/// can never be read with the monitor's fixed 72 h by mistake;</item>
/// <item><b>olderPricePending</b>: pending games whose CHEAPEST stored price is older than <b>ageHours</b>;</item>
/// <item><b>missingPricePending</b>: pending games with no usable comparable price at all. Both are included by
/// default as the user asked; splitting them is what makes the report honest about which half was repriced.</item>
/// </list>
/// </summary>
public sealed record CatalogPriceRefreshOutcome(
    int Pending,
    int Selected,
    int Updated,
    int Skipped,
    int GateDeclined,
    int NoComparablePrice,
    int Unsupported,
    int NotDue,
    int Degraded,
    int Errors,
    int Remaining,
    int UnresolvedWishlistEntries = 0,
    int AgeHours = CatalogPriceRefreshLimits.DefaultAgeHours,
    int OlderPricePending = 0,
    int MissingPricePending = 0);

/// <summary>
/// Provider names of the monitor. The first four are the ones that also carry a consultation stamp on
/// <c>steam_games</c>; <c>steam</c> is the direct snapshot itself.
/// </summary>
public static class CatalogFreshnessProviders
{
    public const string Itad = "itad";

    public const string GgDeals = "ggdeals";

    public const string Epic = "epic";

    public const string Microsoft = "microsoft";

    public const string Steam = "steam";

    /// <summary>
    /// Builds the provider date list of one game: one entry per provider that either reported a usable price
    /// or carries a consultation stamp. A provider with a stamp and no usable price is listed with a null
    /// price date — that is the honest shape of "we asked and there was nothing comparable", and it is what
    /// keeps a Steam-less or unlisted game from reading as a provider failure.
    ///
    /// Stamps and observations in the future are dropped rather than shown: they are corrupt evidence, and a
    /// label of "revisado hace -6 h" is worse than no label.
    /// </summary>
    public static IReadOnlyList<CatalogFreshnessProviderDate> Build(
        IEnumerable<CatalogFreshnessPriceRow> rows,
        CatalogFreshnessSteamRow? steam,
        DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var priceDates = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ObservedAt > asOfUtc)
            {
                continue;
            }

            if (!priceDates.TryGetValue(row.Provider, out var current) || row.ObservedAt > current)
            {
                priceDates[row.Provider] = row.ObservedAt;
            }
        }

        var checkedDates = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        if (steam is not null)
        {
            AddStamp(checkedDates, Itad, steam.OffersRefreshedAt, asOfUtc);
            AddStamp(checkedDates, GgDeals, steam.GgDealsRefreshedAt, asOfUtc);
            AddStamp(checkedDates, Epic, steam.EpicRefreshedAt, asOfUtc);
            AddStamp(checkedDates, Microsoft, steam.MicrosoftRefreshedAt, asOfUtc);
            AddStamp(checkedDates, Steam, steam.ObservedAt, asOfUtc);
        }

        return priceDates.Keys
            .Union(checkedDates.Keys, StringComparer.Ordinal)
            .OrderBy(provider => provider, StringComparer.Ordinal)
            .Select(provider => new CatalogFreshnessProviderDate(
                provider,
                priceDates.TryGetValue(provider, out var observedAt) ? observedAt : null,
                checkedDates.TryGetValue(provider, out var checkedAt) ? checkedAt : null))
            .ToList();
    }

    private static void AddStamp(
        IDictionary<string, DateTime> stamps,
        string provider,
        DateTime? stamp,
        DateTime asOfUtc)
    {
        if (stamp is { } value && value <= asOfUtc)
        {
            stamps[provider] = value;
        }
    }
}

/// <summary>
/// The read-only queries of the monitor and the manual pending-price pass (spec S4/S5/S6). The universe they
/// accept is the one they are given: <see cref="WishlistGames"/> restricts the catalog to a single user's
/// wishlist, which is the only scope both surfaces read. They take
/// <see cref="IQueryable{T}"/> sources instead of a repository so the same builder is used by the service,
/// by the job selection and by <c>checks/CatalogDeals.Checks</c> — over in-memory lists to assert the
/// behaviour, and through <c>ToQueryString()</c> to prove the Npgsql translation offline.
///
/// The basis is one shared price universe: the usable comparable MXN rows of <c>game_offers</c> plus the Steam
/// direct snapshot, past-only. Everything else (buckets, counters, paging, pending selection) is derived from
/// it, so the panel and the job can never disagree about what "pending" means.
/// </summary>
public static class CatalogFreshnessQueries
{
    /// <summary>
    /// Separator of <see cref="CatalogFreshnessPriceRow.StableKey"/>. It is below every character a provider name
    /// or an offer key uses, so comparing the joined value compares the provider first and the key second.
    /// </summary>
    private const string StableKeySeparator = "\u0001";

    /// <summary>
    /// The price universe: every usable comparable MXN price we have, of any age, as long as it was observed
    /// in the past. Provider rows are restricted to the exact MX region; Steam-less games contribute their
    /// offers and nothing else, because they have no Steam snapshot to read.
    /// </summary>
    public static IQueryable<CatalogFreshnessPriceRow> PriceUniverse(
        IQueryable<GameOffer> offers,
        IQueryable<SteamGame> steamGames,
        DateTime asOfUtc)
    {
        var providerRows = offers
            .Where(UsableOffer(asOfUtc))
            .Select(offer => new CatalogFreshnessPriceRow
            {
                GameId = offer.GameId!.Value,
                Provider = offer.Source,
                OfferKey = offer.OfferKey,
                StableKey = offer.Source + StableKeySeparator + offer.OfferKey,
                ShopName = offer.ShopName,
                PricingType = offer.PricingType,
                PriceMinor = offer.MxnCurrentPriceMinor!.Value,
                ObservedAt = offer.ObservedAt,
            });

        var steamRows = steamGames
            .Where(UsableSteamDirect(asOfUtc))
            .Select(game => new CatalogFreshnessPriceRow
            {
                GameId = game.GameId!.Value,
                Provider = CatalogFreshnessProviders.Steam,
                OfferKey = game.AppId.ToString(),
                StableKey = CatalogFreshnessProviders.Steam + StableKeySeparator + game.AppId.ToString(),
                ShopName = CatalogDealQuery.SteamShopName,
                PricingType = CatalogDealQuery.RegionalPricingType,
                PriceMinor = game.CurrentPriceMinor!.Value,
                ObservedAt = game.ObservedAt,
            });

        return providerRows.Concat(steamRows);
    }

    /// <summary>
    /// A usable comparable MXN provider row: the shared price rule of the discover catalog (positive MXN
    /// amount inside the corruption ceiling, not unconverted, not an Epic free row) plus the exact MX region
    /// and the past-only bound. Deliberately no minimum age: an old price is still coverage, which is what
    /// makes `stale` different from `missing`.
    /// </summary>
    public static Expression<Func<GameOffer, bool>> UsableOffer(DateTime asOfUtc)
    {
        var region = CatalogDealQuery.Region;

        return CatalogDealQuery.And<GameOffer>(
            CatalogDealQuery.PricedOffer(),
            CatalogDealQuery.And<GameOffer>(
                offer => offer.GameId != null && offer.Region == region,
                offer => offer.ObservedAt <= asOfUtc));
    }

    /// <summary>The same for the Steam direct snapshot: MX region, MXN quote, a real amount, observed in the past.</summary>
    public static Expression<Func<SteamGame, bool>> UsableSteamDirect(DateTime asOfUtc) =>
        CatalogDealQuery.And<SteamGame>(
            CatalogDealQuery.PricedSteamDirect(),
            game => game.ObservedAt <= asOfUtc);

    /// <summary>
    /// The one price each game is described by: the CHEAPEST usable comparable amount, then — among equal amounts
    /// — the newest observation, then the stable provider/key. Every reduction runs in SQL as an aggregate plus a
    /// join, so the page, the counters, the pending set and the manual pass all read one row per game instead of
    /// the catalog in memory.
    ///
    /// Why not "the newest observation": that was the superseded rule (it made a game with a fresh, expensive
    /// Steam price `recent` while a ten-day-old, much cheaper external offer sat unrefreshed). Why the extra ties:
    /// price alone is not unique, so the classification has to be a total order — otherwise two equal cheap prices
    /// would make the reported shop depend on row order.
    ///
    /// The tie-break aggregate is <c>MIN</c> over a text column: PostgreSQL compares it with the column collation,
    /// and provider names and offer keys are short lowercase ASCII strings, so this order is stable for them.
    ///
    /// Each reduction is its own grouped subquery joined back to the price universe — min amount, then max
    /// observation among the rows at that amount, then min stable key. They are deliberately NOT expressed as a
    /// conditional aggregate (<c>max(observed_at) FILTER (WHERE price = min(price))</c>): PostgreSQL refuses nested
    /// aggregate calls, including aggregates inside a FILTER clause (&quot;aggregate function calls cannot be
    /// nested&quot;), and EF renders that shape happily without proving it valid. Keeping one aggregate per grouped
    /// subquery also keeps each step readable on its own.
    ///
    /// Existence of exactly one winner per game is an invariant of the schema, not a coincidence: offers are unique
    /// per <c>(game_id, region, source, offer_key)</c> and a Steam snapshot per <c>(app_id, region)</c>, which is
    /// exactly the <see cref="CatalogFreshnessPriceRow.StableKey"/> identity. The source of an offer is never the
    /// Steam snapshot's own <c>steam</c> provider (<c>itad</c>, <c>ggdeals</c>, <c>epic</c>, <c>microsoft</c> only),
    /// so the two halves of the universe cannot collide on a key.
    /// </summary>
    public static IQueryable<CatalogFreshnessPriceRow> Winners(IQueryable<CatalogFreshnessPriceRow> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);

        // 1) The cheapest amount of each game, as a grouped subquery.
        var cheapestAmount = prices
            .GroupBy(row => row.GameId)
            .Select(group => new { GameId = group.Key, PriceMinor = group.Min(row => row.PriceMinor) });

        var atCheapestAmount = from row in prices
                               join key in cheapestAmount
                                   on new { row.GameId, row.PriceMinor } equals new { key.GameId, key.PriceMinor }
                               select row;

        // 2) Among the rows at that amount, the newest observation — a second, separate grouped subquery.
        var newestObservation = atCheapestAmount
            .GroupBy(row => row.GameId)
            .Select(group => new { GameId = group.Key, ObservedAt = (DateTime?)group.Max(row => row.ObservedAt) });

        var atNewestObservation = from row in atCheapestAmount
                                  join key in newestObservation
                                      on new { row.GameId, ObservedAt = (DateTime?)row.ObservedAt }
                                      equals new { key.GameId, key.ObservedAt }
                                  select row;

        // 3) Then the stable provider/key, so a full tie is still one deterministic row.
        var firstStableKey = atNewestObservation
            .GroupBy(row => row.GameId)
            .Select(group => new { GameId = group.Key, StableKey = group.Min(row => row.StableKey) });

        return from row in atNewestObservation
               join key in firstStableKey
                   on new { row.GameId, row.StableKey } equals new { key.GameId, key.StableKey }
               select row;
    }

    /// <summary>
    /// Restricts the canonical catalog to one user's wishlist (spec S4). The ids come from
    /// <see cref="CatalogWishlistScopeQuery"/>, i.e. from the requester's own <c>wished</c> rows and their exact
    /// Steam mapping, so the restriction is a bounded <c>ANY</c> in SQL and the library, the favourites and
    /// every other account are excluded by construction. An empty wishlist yields no game at all — never the
    /// whole catalog.
    /// </summary>
    public static IQueryable<Game> WishlistGames(IQueryable<Game> games, IReadOnlyCollection<long> gameIds)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(gameIds);

        return gameIds.Count == 0
            ? games.Where(game => false)
            : games.Where(game => gameIds.Contains(game.GameId));
    }

    /// <summary>
    /// One state per canonical game: the cheapest usable price it has (see <see cref="Winners"/>) and the bucket
    /// that price's instant falls in. The pick, the aggregation and the classification all happen in SQL (three
    /// grouped subqueries plus a <c>CASE</c>), so the monitor pages the catalog instead of materializing it. Every <c>games</c> row appears exactly once, which is what keeps Steam-less games in the
    /// monitor, and the price universe is narrowed to the selected games before it is grouped, so the reductions
    /// never scan prices of games outside the caller's list.
    ///
    /// <paramref name="ageHours"/> is the window the bucket is drawn against (spec S2/S3). The monitor always uses
    /// its fixed <see cref="CatalogFreshness.Hours"/>; the manual pass passes the window the requester chose, so
    /// "pending" means the same thing to both surfaces without the pass inheriting a hidden constant.
    /// </summary>
    public static IQueryable<CatalogFreshnessGameState> GameStates(
        IQueryable<Game> games,
        IQueryable<CatalogFreshnessPriceRow> prices,
        DateTime asOfUtc,
        int ageHours = CatalogFreshness.Hours)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(prices);

        var cutoff = CatalogFreshness.Cutoff(asOfUtc, ageHours);
        var recent = CatalogFreshnessStatus.Recent;
        var stale = CatalogFreshnessStatus.Stale;
        var missing = CatalogFreshnessStatus.Missing;

        // The price universe of the games under analysis only. Without this the four tie-breaking reductions
        // would each sweep the whole catalog's offers before the join discards them.
        var gameIds = games.Select(game => game.GameId);
        var scopedPrices = prices.Where(row => gameIds.Contains(row.GameId));

        var winners = Winners(scopedPrices);

        var observed = from game in games
                       join winner in winners on game.GameId equals winner.GameId
                       select new CatalogFreshnessGameState
                       {
                           GameId = game.GameId,
                           ChosenObservedAt = winner.ObservedAt,
                           ChosenPriceMinor = winner.PriceMinor,
                           ChosenProvider = winner.Provider,
                           ChosenShopName = winner.ShopName,
                           ChosenPricingType = winner.PricingType,
                       };

        // Games with no usable price at all, kept as first-class members: an anti-join, not a client filter.
        var unobserved = games
            .Where(game => !winners.Any(winner => winner.GameId == game.GameId))
            .Select(game => new CatalogFreshnessGameState
            {
                GameId = game.GameId,
                // Assigned explicitly: EF Core refuses a set operation whose branches do not assign the same
                // properties of the projected type.
                ChosenObservedAt = null,
                ChosenPriceMinor = null,
                ChosenProvider = null,
                ChosenShopName = null,
                ChosenPricingType = null,
            });

        // The bucket is a CASE over the plain column the join produced.
        return observed.Concat(unobserved).Select(state => new CatalogFreshnessGameState
        {
            GameId = state.GameId,
            ChosenObservedAt = state.ChosenObservedAt,
            ChosenPriceMinor = state.ChosenPriceMinor,
            ChosenProvider = state.ChosenProvider,
            ChosenShopName = state.ChosenShopName,
            ChosenPricingType = state.ChosenPricingType,
            Status = state.ChosenObservedAt == null
                ? missing
                : state.ChosenObservedAt >= cutoff ? recent : stale,
        });
    }

    /// <summary>The four counters, grouped in SQL. Folding them is <see cref="CatalogFreshnessCounts.Of"/>.</summary>
    public static IQueryable<CatalogFreshnessCount> Counts(IQueryable<CatalogFreshnessGameState> states) =>
        states
            .GroupBy(state => state.Status)
            .Select(group => new CatalogFreshnessCount
            {
                Status = group.Key,
                Total = group.Count(),
            });

    /// <summary>Narrows to the selected filter. <c>all</c> is the identity, so one code path answers every tab.</summary>
    public static IQueryable<CatalogFreshnessGameState> Filter(
        IQueryable<CatalogFreshnessGameState> states,
        string status)
    {
        var normalized = CatalogFreshnessStatus.Parse(status);

        return string.Equals(normalized, CatalogFreshnessStatus.All, StringComparison.Ordinal)
            ? states
            : states.Where(state => state.Status == normalized);
    }

    /// <summary>
    /// One page, ordered "missing first, then oldest chosen price first, then game id" — the order the operator
    /// needs (nothing is more urgent than a game whose cheapest price is old or absent) and a total order, so two
    /// pages never overlap and never drop a game. <c>SKIP/TAKE</c> happens in SQL.
    /// </summary>
    public static IQueryable<CatalogFreshnessGameState> Page(
        IQueryable<CatalogFreshnessGameState> states,
        long skip,
        int pageSize) =>
        states
            .OrderBy(state => state.ChosenObservedAt == null ? 0 : 1)
            .ThenBy(state => state.ChosenObservedAt)
            .ThenBy(state => state.GameId)
            .Skip((int)skip)
            .Take(pageSize);

    /// <summary>
    /// The pending set of the manual price pass: exactly the games the monitor cannot call recent, i.e. the
    /// `stale` and `missing` buckets. Recomputed on every run, from the same universe the panel reads, so a
    /// game that recently got a price stops being selected without anybody clearing a flag.
    /// </summary>
    public static IQueryable<CatalogFreshnessGameState> Pending(IQueryable<CatalogFreshnessGameState> states)
    {
        var recent = CatalogFreshnessStatus.Recent;

        return states.Where(state => state.Status != recent);
    }

    /// <summary>
    /// The one MX <c>steam_games</c> row of each game, chosen in SQL by lowest <c>steam_game_id</c>. A game may
    /// legitimately have more than one app id, and both the monitor and the pass must talk about one row.
    /// </summary>
    public static IQueryable<CatalogFreshnessSteamRow> ChosenSnapshots(IQueryable<SteamGame> steamGames)
    {
        var region = CatalogDealQuery.Region;

        var mxnSnapshots = steamGames.Where(game => game.GameId != null && game.Region == region);

        var chosenIds = mxnSnapshots
            .GroupBy(game => game.GameId)
            .Select(group => new { GameId = group.Key!.Value, SteamGameId = group.Min(game => game.SteamGameId) });

        return from pick in chosenIds
               join snapshot in mxnSnapshots on pick.SteamGameId equals snapshot.SteamGameId
               select new CatalogFreshnessSteamRow
               {
                   GameId = pick.GameId,
                   SteamGameId = snapshot.SteamGameId,
                   AppId = snapshot.AppId,
                   ObservedAt = snapshot.ObservedAt,
                   OffersRefreshedAt = snapshot.OffersRefreshedAt,
                   GgDealsRefreshedAt = snapshot.GgDealsRefreshedAt,
                   EpicRefreshedAt = snapshot.EpicRefreshedAt,
                   MicrosoftRefreshedAt = snapshot.MicrosoftRefreshedAt,
               };
    }

    /// <summary>
    /// Whether at least one price phase of this snapshot would actually run: the Steam direct snapshot, or any
    /// provider stamp, is missing, older than the window, or in the future (corrupt evidence must not park a game
    /// out of the batch). The window is <paramref name="ageHours"/> — the requester's choice for a manual pass, the
    /// monitor's fixed 72 h (and every other caller's default) otherwise.
    ///
    /// This is the named, stricter per-phase evaluation the pass uses on top of the game-coverage buckets
    /// (spec S4/S5). It is what stops a title no shop can price — delisted, no MX offer, no identity match —
    /// from staying `missing` forever, sorting first in every batch and spending a slot on every run while
    /// nothing changed. Such a game keeps its `missing`/`stale` bucket and stays visible in the monitor; it is
    /// simply not re-asked until a gate is due again. A *failed* provider answer does not advance its stamp
    /// (existing semantics), so a degraded attempt is retried on the next run instead of being cooled down.
    ///
    /// The Steam row counts as a phase on purpose: the user asked for pending *prices*, and a Steam direct
    /// price that is five days old next to fresh provider stamps is exactly a pending price. Skipping it would
    /// silently narrow the pass to external stores.
    /// </summary>
    public static Expression<Func<CatalogFreshnessSteamRow, bool>> DuePhaseGate(
        DateTime asOfUtc,
        int ageHours = CatalogFreshness.Hours)
    {
        var cutoff = CatalogFreshness.Cutoff(asOfUtc, ageHours);

        return snapshot =>
            snapshot.ObservedAt < cutoff ||
            snapshot.ObservedAt > asOfUtc ||
            snapshot.OffersRefreshedAt == null ||
            snapshot.OffersRefreshedAt < cutoff ||
            snapshot.OffersRefreshedAt > asOfUtc ||
            snapshot.GgDealsRefreshedAt == null ||
            snapshot.GgDealsRefreshedAt < cutoff ||
            snapshot.GgDealsRefreshedAt > asOfUtc ||
            snapshot.EpicRefreshedAt == null ||
            snapshot.EpicRefreshedAt < cutoff ||
            snapshot.EpicRefreshedAt > asOfUtc ||
            snapshot.MicrosoftRefreshedAt == null ||
            snapshot.MicrosoftRefreshedAt < cutoff ||
            snapshot.MicrosoftRefreshedAt > asOfUtc;
    }

    /// <summary>
    /// The pending games this pass can actually price: those with an MX Steam snapshot whose provider gates are
    /// due, ordered missing first then oldest chosen price first, with the lowest <c>steam_game_id</c> chosen when a
    /// game has more than one.
    ///
    /// "Oldest chosen price" is the same instant the monitor buckets by, so a game whose cheapest price is an old
    /// external offer is selected even while Steam was asked an hour ago (user correction S9). Selection is
    /// recomputed from persisted rows on every run: there is no queue and no pending flag to go stale.
    ///
    /// The due gates use <paramref name="ageHours"/>, the same window the caller's pending set was drawn against, so
    /// a 24 h run and a 168 h run differ in both what they select and when they consider a provider fresh.
    ///
    /// Steam-less pending games are excluded here on purpose and counted by
    /// <see cref="PendingWithoutSteam"/>: their pricing path is the store's own identity (Xbox package family
    /// name), which this pass does not have for a whole wishlist at once. Selecting them would burn a batch slot
    /// on a game that cannot be priced, so the limitation is reported as a counter instead of being hidden.
    /// </summary>
    public static IQueryable<CatalogPriceRefreshCandidate> PendingCandidates(
        IQueryable<CatalogFreshnessGameState> pending,
        IQueryable<SteamGame> steamGames,
        int batchSize,
        DateTime asOfUtc,
        int ageHours = CatalogFreshness.Hours)
    {
        if (batchSize < 1 || batchSize > CatalogPriceRefreshLimits.BatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                $"El lote debe estar entre 1 y {CatalogPriceRefreshLimits.BatchSize} juegos.");
        }

        var dueSnapshots = ChosenSnapshots(steamGames).Where(DuePhaseGate(asOfUtc, ageHours));

        // The hard bound is part of the query, not of the caller: no caller can ask for the unbounded sweep
        // this pass exists to avoid.
        return (from state in pending
                join snapshot in dueSnapshots on state.GameId equals snapshot.GameId
                orderby state.ChosenObservedAt == null ? 0 : 1, state.ChosenObservedAt, state.GameId
                select new CatalogPriceRefreshCandidate
                {
                    GameId = state.GameId,
                    AppId = snapshot.AppId,
                    ChosenPriceMinor = state.ChosenPriceMinor,
                    ChosenObservedAt = state.ChosenObservedAt,
                })
            .Take(batchSize);
    }

    /// <summary>
    /// The pending games whose every provider gate is still fresh for the SAME <paramref name="ageHours"/> the pending
    /// set was drawn against: consulted inside that window and nothing comparable came back. They are the honest
    /// "nothing to re-ask yet" counter of the pass, and they keep their coverage bucket (this is not a way to call
    /// them priced).
    /// </summary>
    public static IQueryable<CatalogFreshnessGameState> PendingNotDue(
        IQueryable<CatalogFreshnessGameState> pending,
        IQueryable<SteamGame> steamGames,
        DateTime asOfUtc,
        int ageHours = CatalogFreshness.Hours)
    {
        var freshSnapshots = ChosenSnapshots(steamGames).Where(NotDuePhaseGate(asOfUtc, ageHours));

        return from state in pending
               join snapshot in freshSnapshots on state.GameId equals snapshot.GameId
               select state;
    }

    /// <summary>
    /// The complement of <see cref="DuePhaseGate"/>: every price phase was answered inside the window, so there
    /// is nothing to re-ask yet.
    /// </summary>
    public static Expression<Func<CatalogFreshnessSteamRow, bool>> NotDuePhaseGate(
        DateTime asOfUtc,
        int ageHours = CatalogFreshness.Hours)
    {
        var due = DuePhaseGate(asOfUtc, ageHours);

        return Expression.Lambda<Func<CatalogFreshnessSteamRow, bool>>(
            Expression.Not(due.Body),
            due.Parameters);
    }

    /// <summary>
    /// The pending games with no MX Steam snapshot: the honest "unsupported" counter of the pass. They stay
    /// pending and visible in the monitor; this job simply has no way to price them.
    /// </summary>
    public static IQueryable<CatalogFreshnessGameState> PendingWithoutSteam(
        IQueryable<CatalogFreshnessGameState> pending,
        IQueryable<SteamGame> steamGames)
    {
        var region = CatalogDealQuery.Region;

        return pending.Where(state => !steamGames
            .Any(game => game.GameId == state.GameId && game.Region == region));
    }

    /// <summary>
    /// Hydration source of the page's Steam rows, one query for the whole page. It is the same "chosen
    /// snapshot" the pass refreshes, so the panel's app id and the job's app id are the same row.
    /// </summary>
    public static IQueryable<CatalogFreshnessSteamRow> SteamRowsForGames(
        IQueryable<SteamGame> steamGames,
        IReadOnlyCollection<long> gameIds) =>
        ChosenSnapshots(steamGames).Where(row => gameIds.Contains(row.GameId));

    /// <summary>Hydration source of the page's price rows, one query for the whole page.</summary>
    public static IQueryable<CatalogFreshnessPriceRow> PriceRowsForGames(
        IQueryable<CatalogFreshnessPriceRow> prices,
        IReadOnlyCollection<long> gameIds) =>
        prices.Where(row => gameIds.Contains(row.GameId));
}
