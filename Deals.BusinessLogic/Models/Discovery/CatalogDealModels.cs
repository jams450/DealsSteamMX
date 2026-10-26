using System.Globalization;
using System.Linq.Expressions;
using Deals.BusinessLogic.Models.Notifications;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>
/// The three stable category tokens of the discover catalog (spec S3/L1). They are the names of the three
/// independent OR rules of <see cref="DealAlertPolicy"/>, not a severity scale and not a funnel:
/// a game can be in `historic` while costing more than the cheap threshold, and in `score` without being
/// cheap at all. There is deliberately no `discount` or `recent` token — neither is one of the rules.
/// </summary>
public static class CatalogDealCategories
{
    /// <summary>Rule 2: strictly below <see cref="DealAlertPolicy.CheapPriceThresholdMinor"/> (50.00 MXN).</summary>
    public const string Under50 = "under50";

    /// <summary>Rule 3: at a verified provider history low in the offer's own currency.</summary>
    public const string Historic = "historic";

    /// <summary>Rule 1: the shared wishlist deal score is strictly greater than 7.</summary>
    public const string Score = "score";

    /// <summary>Stable order, used by the tab strip and every count in the envelope.</summary>
    public static readonly IReadOnlyList<string> All = [Under50, Historic, Score];

    public static bool TryParse(string? raw, out string category)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case Under50:
                category = Under50;
                return true;
            case Historic:
                category = Historic;
                return true;
            case Score:
                category = Score;
                return true;
            default:
                category = Under50;
                return false;
        }
    }

    /// <summary>Normalizes the query token, or throws the same shape of Spanish error the API already uses.</summary>
    public static string Parse(string? raw) =>
        TryParse(raw, out var category)
            ? category
            : throw new ArgumentException(
                $"Categoría desconocida: '{raw}'. Valores válidos: under50, historic, score.",
                nameof(raw));
}

/// <summary>
/// Pagination bounds of the discover catalog. Page numbers are 1-based; an invalid page is page 1 and an
/// oversized page size is clamped, so a caller can never ask for the whole catalog in one request.
/// </summary>
public static class CatalogDealPager
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxPage = 1_000_000;

    public static int NormalizePage(int page) => page < 1 ? 1 : Math.Min(page, MaxPage);

    public static int NormalizePageSize(int pageSize) =>
        pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
}

/// <summary>
/// One persisted price observation as the catalog query sees it: either a comparable <c>game_offers</c> row
/// or the Steam direct current price of the game's MX <c>steam_games</c> row. It is a bounded projection
/// (no payload blobs, no provider objects), which is what lets the catalog page in SQL instead of
/// materializing every fresh offer.
///
/// It is built with an object initializer, not a positional constructor, on purpose: EF Core 9 refuses a set
/// operation whose operands project a positional constructor ("Unable to translate set operation after client
/// projection has been applied"), while a member-init projection translates. The catalog's whole price universe
/// is one <c>UNION ALL</c> of provider rows and the Steam direct snapshot, so the shape is part of the contract
/// and <c>checks/CatalogDeals.Checks</c> renders it to prove it.
///
/// The Steam direct row carries no provider history (<see cref="HistoryLowAllMinor"/> is null), so it can
/// never qualify as historic: our own observation of a Steam price is not history-low evidence. Its
/// <see cref="OfferKey"/> is the app id of that row, the same key the notifier uses for a Steam candidate, so
/// the two surfaces build the same dedup identity.
/// </summary>
public sealed record CatalogDealOfferRow
{
    public long GameId { get; init; }

    public int? SteamGameId { get; init; }

    public string Source { get; init; } = string.Empty;

    public string OfferKey { get; init; } = string.Empty;

    public string ShopName { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string PricingType { get; init; } = string.Empty;

    public string OriginalCurrency { get; init; } = string.Empty;

    public int? OriginalCurrentPriceMinor { get; init; }

    public int MxnCurrentPriceMinor { get; init; }

    public int? HistoryLowAllMinor { get; init; }

    public string? HistoryLowCurrency { get; init; }

    public DateTime ObservedAt { get; init; }

    public string? DealUrl { get; init; }
}

/// <summary>
/// One category card: the game identity plus the ONE offer that qualified in that category. Price, shop
/// label, classification, pricing type, history low and URL always come from that same row, so a card can
/// never show one store's name over another store's price (the mislabelling bug this repo shipped four
/// times). <see cref="UsesFxEstimate"/> is derived from the row's pricing type, which is what the UI needs
/// to render the `≈` on a converted amount.
/// </summary>
public sealed record CatalogDealItem(
    long GameId,
    int? SteamAppId,
    string Title,
    string? ImageUrl,
    string Source,
    string ShopName,
    string Classification,
    string PricingType,
    int MxnPriceMinor,
    int? OriginalPriceMinor,
    string OriginalCurrency,
    bool UsesFxEstimate,
    int? HistoryLowMinor,
    string? HistoryLowCurrency,
    string? DealUrl,
    double? Score,
    IReadOnlyList<string> Reasons,
    DateTime ObservedAt);

/// <summary>
/// One page of one category plus the three tab totals, so the UI needs a single round-trip to paint the tab
/// counters and the selected list (spec L2: no endpoint per count). <see cref="Count"/> is how many cards
/// this page returned; <see cref="Total"/> is how many games the selected category holds in the catalog.
/// </summary>
public sealed record CatalogDealPage(
    string Category,
    IReadOnlyList<CatalogDealItem> Items,
    int Count,
    int Total,
    int Under50Total,
    int HistoricTotal,
    int ScoreTotal,
    int Page,
    int PageSize,
    bool HasMore);

/// <summary>
/// The server-side predicates the catalog query composes. Every one of them is a plain field comparison with
/// the column bare (no function on an indexed column, no client-side evaluation), and every one of them is
/// pinned to the shared policy by <c>checks/CatalogDeals.Checks</c>: the checks compile them and assert they
/// agree with <see cref="DealAlertPolicy"/> row by row. That is the only protection available here — these
/// checks are forbidden a database, so the EF translation of the composed query itself is not covered.
/// </summary>
public static class CatalogDealQuery
{
    /// <summary>Country of every catalog price. The comparator is MX-only.</summary>
    public const string Region = "mx";

    public const string MxnCurrency = "MXN";

    public const string SteamSource = "steam";

    public const string EpicSource = "epic";

    public const string SteamShopName = "Steam";

    public const string OfficialClassification = "official";

    public const string RegionalPricingType = GlobalDealPricingTypes.Regional;

    private const string UnconvertedPricing = GlobalDealPricingTypes.Unconverted;

    /// <summary>
    /// Price portion of <see cref="DealAlertPolicy.IsComparable"/>: a positive MXN amount inside the
    /// corruption ceiling, a pricing type other than `unconverted`, and no Epic free or price-less row. The
    /// region restriction and the freshness window are applied by the callers.
    ///
    /// This half is public because the catalog freshness monitor asks the same question with a *different*
    /// time window (and with no window at all): a price that is old is still a price, which is exactly the
    /// distinction between `stale` and `missing`. Keeping one predicate is the only way the monitor and the
    /// discover catalog cannot disagree about which rows count as an MXN price.
    /// </summary>
    public static Expression<Func<GameOffer, bool>> PricedOffer()
    {
        var ceiling = DealAlertPolicy.MaxPriceMinor;

        return offer => offer.MxnCurrentPriceMinor != null &&
            offer.MxnCurrentPriceMinor > 0 &&
            offer.MxnCurrentPriceMinor <= ceiling &&
            offer.PricingType != UnconvertedPricing &&
            !(offer.Source == EpicSource &&
                (offer.OriginalCurrentPriceMinor == null || offer.OriginalCurrentPriceMinor <= 0));
    }

    /// <summary>
    /// Freshness portion of <see cref="DealAlertPolicy.IsComparable"/>: observed in the past, no older than
    /// the window. A future stamp is corrupt evidence and is refused here, never read as fresh.
    /// </summary>
    public static Expression<Func<GameOffer, bool>> FreshOffer(DateTime nowUtc, TimeSpan maxSnapshotAge)
    {
        var threshold = nowUtc - maxSnapshotAge;

        return offer => offer.ObservedAt >= threshold && offer.ObservedAt <= nowUtc;
    }

    /// <summary>
    /// Price and freshness portion of <see cref="DealAlertPolicy.IsComparable"/>: a positive MXN amount
    /// inside the corruption ceiling, a pricing type other than `unconverted`, no Epic free or price-less
    /// row, and a fresh observation. The shared region restriction is applied by CanonicalOffer.
    /// </summary>
    public static Expression<Func<GameOffer, bool>> ComparableOffer(DateTime nowUtc, TimeSpan maxSnapshotAge) =>
        And(PricedOffer(), FreshOffer(nowUtc, maxSnapshotAge));

    /// <summary>Shared comparability including exact MX region, plus the catalog's canonical anchor.</summary>
    public static Expression<Func<GameOffer, bool>> CanonicalOffer(DateTime nowUtc, TimeSpan maxSnapshotAge)
    {
        var region = Region;

        return And(
            offer => offer.GameId != null && offer.Region == region,
            ComparableOffer(nowUtc, maxSnapshotAge));
    }

    /// <summary>
    /// The Steam direct price of the game's MX snapshot, without any time bound: region, an MXN quote, a
    /// positive amount inside the corruption ceiling. The window is the caller's business, exactly like
    /// <see cref="PricedOffer"/>.
    /// </summary>
    public static Expression<Func<SteamGame, bool>> PricedSteamDirect()
    {
        var ceiling = DealAlertPolicy.MaxPriceMinor;
        var region = Region;
        var currency = MxnCurrency;

        return game => game.GameId != null &&
            game.Region == region &&
            game.Currency == currency &&
            game.CurrentPriceMinor != null &&
            game.CurrentPriceMinor > 0 &&
            game.CurrentPriceMinor <= ceiling;
    }

    /// <summary>
    /// The Steam direct price of the game's MX snapshot, gated exactly like a provider row: MX region, an
    /// MXN quote, a positive amount inside the corruption ceiling and a fresh observation. Its own
    /// <c>lowest_price_minor</c> is never read: that value is created by our own observations.
    /// </summary>
    public static Expression<Func<SteamGame, bool>> SteamDirect(DateTime nowUtc, TimeSpan maxSnapshotAge)
    {
        var threshold = nowUtc - maxSnapshotAge;

        return And(PricedSteamDirect(), game => game.ObservedAt >= threshold && game.ObservedAt <= nowUtc);
    }

    /// <summary>Rule 2 on a provider row: the exact predicate of <see cref="DealAlertReasons.Price"/>.</summary>
    public static Expression<Func<GameOffer, bool>> CheapPrice()
    {
        var threshold = DealAlertPolicy.CheapPriceThresholdMinor;

        return offer => offer.MxnCurrentPriceMinor != null && offer.MxnCurrentPriceMinor < threshold;
    }

    /// <summary>Rule 2 on the Steam direct row.</summary>
    public static Expression<Func<SteamGame, bool>> CheapPriceSteam()
    {
        var threshold = DealAlertPolicy.CheapPriceThresholdMinor;

        return game => game.CurrentPriceMinor != null && game.CurrentPriceMinor < threshold;
    }

    /// <summary>
    /// Rule 3 on a provider row: the exact predicate of <see cref="DealAlertPolicy.HasVerifiedHistoryLow"/>.
    /// A Steam row is refused outright and the low must be quoted in the row's own currency, so no FX
    /// conversion and no cross-source comparison can manufacture a minimum.
    /// </summary>
    public static Expression<Func<GameOffer, bool>> VerifiedHistoryLow() =>
        offer => offer.Source != SteamSource &&
            offer.HistoryLowAllMinor != null &&
            offer.HistoryLowAllMinor > 0 &&
            offer.HistoryLowCurrency != null &&
            offer.HistoryLowCurrency != "" &&
            offer.HistoryLowCurrency == offer.OriginalCurrency &&
            offer.OriginalCurrentPriceMinor != null &&
            offer.OriginalCurrentPriceMinor > 0 &&
            offer.OriginalCurrentPriceMinor <= offer.HistoryLowAllMinor;

    /// <summary>
    /// Necessary (not sufficient) condition of the score rule, used as the SQL pre-filter: a discount of at
    /// most 50% yields a discount factor of 0, so the score can reach 3 at most and can never exceed 7. The
    /// remaining exact evaluation is the shared <see cref="DealAlertPolicy.DealScore"/> over the game's MXN
    /// Steam base and its provider MXN history low — the formula is never re-implemented in SQL.
    ///
    /// The multiplication is widened to <see cref="long"/> on purpose: the comparability ceiling is 500,000,000
    /// minor units and an <c>int</c> product of a corrupt price would overflow to a negative number and turn
    /// the necessary filter into a false positive.
    /// </summary>
    public static bool ScoreNeedsMoreThanHalfOff(int priceMinor, int basePriceMinor) =>
        priceMinor * 2L < basePriceMinor;

    /// <summary>The same necessary condition over the Steam direct row of a game, widened the same way.</summary>
    public static Expression<Func<SteamGame, bool>> ScoreNeedsMoreThanHalfOffSteam() =>
        game => game.CurrentPriceMinor != null && game.CurrentPriceMinor * 2L < game.InitialPriceMinor;

    /// <summary>
    /// Combines two predicates into one, rebinding both to a single parameter. Public because the freshness
    /// monitor composes these same predicate fragments with a different window instead of copying them.
    /// </summary>
    public static Expression<Func<T, bool>> And<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(T), "item");
        var body = Expression.AndAlso(
            Replace(left.Body, left.Parameters[0], parameter),
            Replace(right.Body, right.Parameters[0], parameter));

        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static Expression Replace(Expression body, ParameterExpression from, ParameterExpression to) =>
        new ParameterReplacer(from, to).Visit(body)!;

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>
/// One candidate game of the score category as SQL produces it. The fields are nullable on purpose: the
/// projection must not need <c>Nullable.Value</c> inside the expression tree, and the filters above it
/// guarantee every value. Null fields mean "this row cannot carry a score" and are dropped. Object initializer,
/// like every other projected shape here: EF Core refuses a positional constructor inside a set operation.
/// </summary>
public sealed record CatalogDealScoreCandidate
{
    public long? GameId { get; init; }

    public int SteamGameId { get; init; }

    public int? BasePriceMinor { get; init; }

    public int? MinPriceMinor { get; init; }
}

/// <summary>One game id with its cheapest qualifying price, the shape of a category page.</summary>
public sealed record CatalogDealGamePage
{
    public long GameId { get; init; }

    public int BestPriceMinor { get; init; }
}

/// <summary>One <c>steam_games</c> row's provider-reported history low in MXN, keyed by the Steam snapshot.</summary>
public sealed record CatalogDealHistoryLow
{
    public int? SteamGameId { get; init; }

    public int? Low { get; init; }
}

/// <summary>
/// The actual EF query builders of the catalog, factored out so <c>checks/CatalogDeals.Checks</c> can render every
/// one of them with <c>ToQueryString()</c> (offline: no connection, no query execution) and prove the translation
/// before anyone claims it. They take <see cref="IQueryable{T}"/> sources instead of a repository, so the same
/// builder is usable from the service and from a check-owned <c>AppDbContext</c>.
///
/// Ordering note: <see cref="CategoryGameIds"/> and <see cref="CategoryPage"/> share one deterministic order
/// (<c>min(price), game_id</c>), which is what makes the tab total and the pages agree with each other.
/// </summary>
public static class CatalogDealQueries
{
    /// <summary>
    /// The provider-row projection, exposed as an expression so a check can compile the exact same mapping and
    /// compare the resulting row with the notifier's candidate.
    /// </summary>
    public static Expression<Func<GameOffer, CatalogDealOfferRow>> ProviderRowProjection() =>
        offer => new CatalogDealOfferRow
        {
            GameId = offer.GameId!.Value,
            SteamGameId = offer.SteamGameId,
            Source = offer.Source,
            OfferKey = offer.OfferKey,
            ShopName = offer.ShopName,
            Classification = offer.Classification,
            PricingType = offer.PricingType,
            OriginalCurrency = offer.OriginalCurrency,
            OriginalCurrentPriceMinor = offer.OriginalCurrentPriceMinor,
            MxnCurrentPriceMinor = offer.MxnCurrentPriceMinor!.Value,
            HistoryLowAllMinor = offer.HistoryLowAllMinor,
            HistoryLowCurrency = offer.HistoryLowCurrency,
            ObservedAt = offer.ObservedAt,
            DealUrl = offer.DealUrl,
        };

    /// <summary>
    /// Comparable canonical provider rows of one category (or of no category, for hydration). Every filter is
    /// on the entity, before the projection, so freshness and region stay bare-column comparisons.
    /// </summary>
    public static IQueryable<CatalogDealOfferRow> ProviderRows(
        IQueryable<GameOffer> offers,
        DealAlertContext context,
        string? category)
    {
        ArgumentNullException.ThrowIfNull(context);

        var filtered = offers.Where(CatalogDealQuery.CanonicalOffer(context.NowUtc, context.MaxSnapshotAge));

        if (string.Equals(category, CatalogDealCategories.Under50, StringComparison.Ordinal))
        {
            filtered = filtered.Where(CatalogDealQuery.CheapPrice());
        }
        else if (string.Equals(category, CatalogDealCategories.Historic, StringComparison.Ordinal))
        {
            filtered = filtered.Where(CatalogDealQuery.VerifiedHistoryLow());
        }

        return filtered.Select(ProviderRowProjection());
    }

    /// <summary>
    /// The Steam direct projection, exposed for the same reason as <see cref="ProviderRowProjection"/>.
    /// </summary>
    public static Expression<Func<SteamGame, CatalogDealOfferRow>> SteamDirectRowProjection() =>
        game => new CatalogDealOfferRow
        {
            GameId = game.GameId!.Value,
            SteamGameId = game.SteamGameId,
            Source = CatalogDealQuery.SteamSource,
            OfferKey = game.AppId.ToString(),
            ShopName = CatalogDealQuery.SteamShopName,
            Classification = CatalogDealQuery.OfficialClassification,
            PricingType = CatalogDealQuery.RegionalPricingType,
            OriginalCurrency = CatalogDealQuery.MxnCurrency,
            OriginalCurrentPriceMinor = game.CurrentPriceMinor,
            MxnCurrentPriceMinor = game.CurrentPriceMinor!.Value,
            HistoryLowAllMinor = null,
            HistoryLowCurrency = null,
            ObservedAt = game.ObservedAt,
            DealUrl = null,
        };

    /// <summary>
    /// The Steam direct snapshot as a price row. It carries no provider history low (our own observations
    /// created the local minimum), so it can never qualify as historic. The offer key is the app id, which is
    /// exactly the key the notifier uses for a Steam candidate, so both surfaces build the same dedup identity.
    /// </summary>
    public static IQueryable<CatalogDealOfferRow> SteamDirectRows(
        IQueryable<SteamGame> steamGames,
        DealAlertContext context,
        string? category)
    {
        ArgumentNullException.ThrowIfNull(context);

        var filtered = steamGames.Where(CatalogDealQuery.SteamDirect(context.NowUtc, context.MaxSnapshotAge));

        if (string.Equals(category, CatalogDealCategories.Under50, StringComparison.Ordinal))
        {
            filtered = filtered.Where(CatalogDealQuery.CheapPriceSteam());
        }

        return filtered.Select(SteamDirectRowProjection());
    }

    /// <summary>
    /// The whole price universe of the catalog: provider rows plus Steam direct, unless the category is
    /// <c>historic</c> (Steam has no provider history low, so it can never belong there).
    /// </summary>
    public static IQueryable<CatalogDealOfferRow> Universe(
        IQueryable<GameOffer> offers,
        IQueryable<SteamGame> steamGames,
        DealAlertContext context,
        string? category = null)
    {
        var providerRows = ProviderRows(offers, context, category);

        return string.Equals(category, CatalogDealCategories.Historic, StringComparison.Ordinal)
            ? providerRows
            : providerRows.Concat(SteamDirectRows(steamGames, context, category));
    }

    /// <summary>The count source of a category: one row per distinct game that holds a qualifying offer.</summary>
    public static IQueryable<long> CategoryGameIds(IQueryable<CatalogDealOfferRow> universe) =>
        universe.Select(row => row.GameId).Distinct();

    /// <summary>
    /// One category page: cheapest qualifying offer per game, ordered by that price and then by game id.
    /// <c>SKIP/TAKE</c> is applied in SQL, so nothing outside the page is materialized.
    /// </summary>
    public static IQueryable<CatalogDealGamePage> CategoryPage(
        IQueryable<CatalogDealOfferRow> universe,
        long skip,
        int pageSize) =>
        universe
            .GroupBy(row => row.GameId)
            .Select(group => new CatalogDealGamePage
            {
                GameId = group.Key,
                BestPriceMinor = group.Min(row => row.MxnCurrentPriceMinor),
            })
            .OrderBy(page => page.BestPriceMinor)
            .ThenBy(page => page.GameId)
            .Skip((int)skip)
            .Take(pageSize);

    /// <summary>Hydration source: the universe rows of the games on the page, and of nothing else.</summary>
    public static IQueryable<CatalogDealOfferRow> RowsForGames(
        IQueryable<CatalogDealOfferRow> universe,
        IReadOnlyCollection<long> gameIds) =>
        universe.Where(row => gameIds.Contains(row.GameId));

    /// <summary>The game's MXN Steam snapshot: the only legitimate score base, never a converted foreign price.</summary>
    public static IQueryable<SteamGame> MxnSteamSnapshots(IQueryable<SteamGame> steamGames)
    {
        var region = CatalogDealQuery.Region;
        var currency = CatalogDealQuery.MxnCurrency;

        return steamGames
            .Where(game => game.Region == region && game.Currency == currency)
            .Where(game => game.GameId != null && game.InitialPriceMinor != null && game.InitialPriceMinor > 0);
    }

    /// <summary>
    /// Candidate games of the score category from provider rows, joined to the Steam snapshot of the row's own
    /// <c>steam_game_id</c> — the same snapshot the notifier reads, so the base price cannot come from another
    /// app id of the same game. The <c>where</c> clause is only the necessary condition of the rule
    /// (<see cref="CatalogDealQuery.ScoreNeedsMoreThanHalfOff"/>); the exact rule is evaluated afterwards.
    /// </summary>
    public static IQueryable<CatalogDealScoreCandidate> ScoreProviderCandidates(
        IQueryable<GameOffer> offers,
        IQueryable<SteamGame> mxnSteamSnapshots,
        DealAlertContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return from offer in offers.Where(CatalogDealQuery.CanonicalOffer(context.NowUtc, context.MaxSnapshotAge))
               join game in mxnSteamSnapshots
                   on new { Id = offer.SteamGameId } equals new { Id = (int?)game.SteamGameId }
               where offer.MxnCurrentPriceMinor != null && offer.MxnCurrentPriceMinor * 2L < game.InitialPriceMinor
               group offer.MxnCurrentPriceMinor by new { offer.GameId, game.SteamGameId, game.InitialPriceMinor }
               into grouped
               select new CatalogDealScoreCandidate
               {
                   GameId = grouped.Key.GameId,
                   SteamGameId = grouped.Key.SteamGameId,
                   BasePriceMinor = grouped.Key.InitialPriceMinor,
                   MinPriceMinor = grouped.Min(),
               };
    }

    /// <summary>Candidate games of the score category from the Steam direct snapshot itself.</summary>
    public static IQueryable<CatalogDealScoreCandidate> ScoreSteamDirectCandidates(
        IQueryable<SteamGame> steamGames,
        DealAlertContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return steamGames
            .Where(CatalogDealQuery.SteamDirect(context.NowUtc, context.MaxSnapshotAge))
            .Where(game => game.InitialPriceMinor != null && game.InitialPriceMinor > 0)
            .Where(CatalogDealQuery.ScoreNeedsMoreThanHalfOffSteam())
            .Select(game => new CatalogDealScoreCandidate
            {
                GameId = game.GameId,
                SteamGameId = game.SteamGameId,
                BasePriceMinor = game.InitialPriceMinor,
                MinPriceMinor = game.CurrentPriceMinor,
            });
    }

    /// <summary>
    /// Provider-reported history lows in MXN, keyed by the exact <c>steam_game_id</c> — the same query and the
    /// same key the notifier uses, so a card's score input and a notification's score input are the same number.
    /// No row of another currency can enter (nothing is converted) and a Steam-less game has no key at all.
    /// </summary>
    public static IQueryable<CatalogDealHistoryLow> HistoryLows(
        IQueryable<GameOffer> offers,
        IReadOnlyCollection<int> steamGameIds)
    {
        var currency = CatalogDealQuery.MxnCurrency;

        return offers
            .Where(offer => offer.SteamGameId != null && steamGameIds.Contains(offer.SteamGameId.Value))
            .GroupBy(offer => offer.SteamGameId)
            .Select(group => new CatalogDealHistoryLow
            {
                SteamGameId = group.Key,
                Low = group.Min(offer => offer.HistoryLowCurrency == currency ? offer.HistoryLowAllMinor : null),
            });
    }
}

/// <summary>
/// Category selection over the shared policy's decisions. The order of the two steps is the point:
/// an offer is first asked whether it satisfies the category's rule, and only the qualifying offers of a
/// game compete for "best". Taking the cheapest offer of the game first and then asking whether it qualifies
/// would hide a game from `historic` whenever a cheaper non-historic offer existed.
/// </summary>
public static class CatalogDealSelection
{
    /// <summary>The reason a category stands for. Unknown tokens are rejected, never defaulted.</summary>
    public static string ReasonOf(string category)
    {
        if (CatalogDealCategories.TryParse(category, out var normalized))
        {
            return normalized switch
            {
                CatalogDealCategories.Under50 => DealAlertReasons.Price,
                CatalogDealCategories.Historic => DealAlertReasons.HistoryLow,
                _ => DealAlertReasons.Score,
            };
        }

        throw new ArgumentException(
            $"Categoría desconocida: '{category}'. Valores válidos: under50, historic, score.",
            nameof(category));
    }

    /// <summary>True only when the shared policy fired <em>and</em> that rule is the requested category's.</summary>
    public static bool IsInCategory(DealAlertDecision decision, string category)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Triggered && decision.Reasons.Contains(ReasonOf(category));
    }

    /// <summary>Maps one persisted row onto the notifier's candidate, so both surfaces share one policy call.</summary>
    public static DealAlertCandidate ToCandidate(
        CatalogDealOfferRow row,
        SteamGame? steam,
        Game? game,
        int? mxnHistoryLowMinor)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The snapshot must be the row's own steam_game_id. The service looks it up by that exact id, and the
        // notifier keys its own dictionary the same way: a different app id of the same game is a different
        // price, a different base and a different dedup key, so it is refused instead of silently used.
        if (steam is not null && steam.SteamGameId != row.SteamGameId)
        {
            throw new ArgumentException(
                $"Steam snapshot {steam.SteamGameId} does not belong to offer row's steam_game_id {row.SteamGameId?.ToString() ?? "none"}.",
                nameof(steam));
        }

        return new DealAlertCandidate(
            row.GameId,
            row.SteamGameId,
            !string.IsNullOrWhiteSpace(steam?.Name) ? steam!.Name : game?.Title ?? row.ShopName,
            row.Source,
            ResolveOfferKey(row, steam),
            row.ShopName,
            row.Classification,
            row.PricingType,
            CatalogDealQuery.Region,
            row.OriginalCurrency,
            row.OriginalCurrentPriceMinor,
            row.MxnCurrentPriceMinor,
            row.HistoryLowAllMinor,
            row.HistoryLowCurrency,
            row.ObservedAt,
            steam?.InitialPriceMinor,
            steam?.Currency,
            mxnHistoryLowMinor,
            mxnHistoryLowMinor is null ? null : CatalogDealQuery.MxnCurrency,
            ResolveDealUrl(row, steam?.AppId));
    }

    /// <summary>
    /// The offer key used for dedup identity. A provider row keeps the key it persisted; a Steam direct row is
    /// keyed by its app id, exactly like the notifier's Steam candidate.
    /// </summary>
    public static string ResolveOfferKey(CatalogDealOfferRow row, SteamGame? steam)
    {
        ArgumentNullException.ThrowIfNull(row);

        return string.Equals(row.Source, CatalogDealQuery.SteamSource, StringComparison.Ordinal) && steam is not null
            ? steam.AppId.ToString(CultureInfo.InvariantCulture)
            : row.OfferKey;
    }

    /// <summary>
    /// The URL of a card. A provider row keeps the URL it persisted, verbatim; a row with no persisted URL
    /// yields null rather than a guessed link. Only a Steam direct row — which is a Steam price by
    /// construction — resolves to the Steam store page of its own app id.
    /// </summary>
    public static string? ResolveDealUrl(CatalogDealOfferRow row, int? steamAppId)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!string.IsNullOrWhiteSpace(row.DealUrl))
        {
            return row.DealUrl;
        }

        return string.Equals(row.Source, CatalogDealQuery.SteamSource, StringComparison.Ordinal) && steamAppId is > 0
            ? SteamStoreUrl(steamAppId.Value)
            : null;
    }

    public static string SteamStoreUrl(int appId) =>
        $"https://store.steampowered.com/app/{appId.ToString(CultureInfo.InvariantCulture)}/";

    /// <summary>
    /// One card per canonical game for the requested category, ordered by best price and then game id.
    /// Ties are resolved by source, offer key, shop name, url and finally the most recent observation, so the
    /// same persisted snapshot always produces the same card regardless of row or input order.
    /// </summary>
    public static IReadOnlyList<DealAlertDecision> BestPerGame(
        IEnumerable<DealAlertDecision> decisions,
        string category)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var reason = ReasonOf(category);

        var best = new Dictionary<long, DealAlertDecision>();
        foreach (var decision in decisions)
        {
            if (!decision.Triggered || !decision.Reasons.Contains(reason))
            {
                continue;
            }

            if (CardKey(decision.Candidate) is not { } key)
            {
                continue;
            }

            if (!best.TryGetValue(key, out var current) || Compare(decision.Candidate, current.Candidate) < 0)
            {
                best[key] = decision;
            }
        }

        return best
            .Values
            .OrderBy(decision => decision.Candidate.MxnCurrentPriceMinor)
            .ThenBy(decision => CardKey(decision.Candidate) ?? 0)
            .ToList();
    }

    /// <summary>Builds the card from the winning decision; every field is read from that one decision.</summary>
    public static CatalogDealItem ToItem(
        DealAlertDecision decision,
        string title,
        string? imageUrl,
        int? steamAppId)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var offer = decision.Candidate;

        return new CatalogDealItem(
            offer.GameId ?? 0,
            steamAppId,
            title,
            imageUrl,
            offer.Source,
            offer.ShopName,
            offer.Classification,
            offer.PricingType,
            offer.MxnCurrentPriceMinor,
            offer.OriginalCurrentPriceMinor,
            offer.OriginalCurrency,
            string.Equals(offer.PricingType, GlobalDealPricingTypes.FxEstimate, StringComparison.Ordinal),
            offer.HistoryLowAllMinor,
            offer.HistoryLowCurrency,
            offer.DealUrl,
            decision.Score,
            decision.Reasons,
            offer.ObservedAt);
    }

    private static long? CardKey(DealAlertCandidate offer) =>
        offer.GameId ?? (offer.SteamGameId is { } steamGameId ? -steamGameId : null);

    private static int Compare(DealAlertCandidate left, DealAlertCandidate right)
    {
        var price = left.MxnCurrentPriceMinor.CompareTo(right.MxnCurrentPriceMinor);
        if (price != 0)
        {
            return price;
        }

        var source = string.CompareOrdinal(left.Source, right.Source);
        if (source != 0)
        {
            return source;
        }

        var offerKey = string.CompareOrdinal(left.OfferKey, right.OfferKey);
        if (offerKey != 0)
        {
            return offerKey;
        }

        var shop = string.CompareOrdinal(left.ShopName, right.ShopName);
        if (shop != 0)
        {
            return shop;
        }

        var url = string.CompareOrdinal(left.DealUrl, right.DealUrl);
        if (url != 0)
        {
            return url;
        }

        return right.ObservedAt.CompareTo(left.ObservedAt);
    }
}
