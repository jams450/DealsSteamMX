using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Notifications;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Read-only discover catalog over the persisted price snapshots: three bounded, independently paged
/// categories (<see cref="CatalogDealCategories"/>) plus the three tab totals in one envelope.
///
/// What it reads: comparable <c>game_offers</c> rows of the canonical catalog (<c>game_id</c> present, MX
/// region, fresh) and the Steam direct current price of the game's MX <c>steam_games</c> row. What it never
/// does: call a provider, write a price, read a bundle, touch identity, or read the environment. Steam-less
/// games are first-class members — an offer that hangs off <c>game_id</c> alone has no Steam anchor and still
/// gets a card, with its own provider URL.
///
/// Eligibility is not re-invented here. Every offer is mapped onto the notifier's
/// <see cref="DealAlertCandidate"/> and decided by <see cref="DealAlertPolicy"/>; a category admits an offer
/// exactly when the shared decision carries that category's reason. The SQL builders live in
/// <see cref="CatalogDealQueries"/> and are pinned two ways: their predicates are asserted against the policy
/// row by row, and every one of them is rendered with <c>ToQueryString()</c> by
/// <c>checks/CatalogDeals.Checks</c> so a translation failure is observed offline instead of claimed away.
///
/// Parity with the notifier is structural, not incidental. The Steam snapshot and the provider MXN history low
/// are looked up by the offer row's **exact** <c>steam_game_id</c> (never the game's first Steam row), so the
/// base price, the history low and the dedup identity of a card are the same values the notifier computes.
///
/// Query shape (spec S6: bounded, no unbounded materialization):
///   * counts and the <c>under50</c>/<c>historic</c> pages are answered inside Postgres — a
///     <c>DISTINCT game_id</c> count and a <c>GROUP BY game_id</c> with a deterministic
///     <c>ORDER BY min(price), game_id</c> and <c>LIMIT</c>;
///   * the <c>score</c> category cannot be answered from one row (the formula needs the game's MXN Steam base
///     and its provider MXN history low), so its SQL side is a necessary pre-filter — more than half off the
///     MXN Steam base — and the exact evaluation is the shared <see cref="DealAlertPolicy.DealScore"/>. The
///     pre-filter is a superset, never a substitute: the formula is not duplicated in SQL, so a catalog card
///     and a notification can never disagree about a score;
///   * only the chosen page's games are hydrated (offers, game rows, Steam snapshots, provider MXN lows),
///     which is at most <see cref="CatalogDealPager.MaxPageSize"/> games per request.
///
/// Prices are read from persisted snapshots, so every card carries the instant the offer was observed. The
/// freshness window comes from the caller's <see cref="DealAlertContext.MaxSnapshotAge"/> — the same 72h
/// (configurable) window the notifier uses — and the score threshold from
/// <see cref="DealAlertContext.MinViableDiscountPercent"/>.
/// </summary>
public sealed class CatalogDealService(IRepository repository)
{
    /// <summary>
    /// One page of the requested category. <paramref name="category"/> is one of
    /// <see cref="CatalogDealCategories"/>; an unknown token throws <see cref="ArgumentException"/>, never a
    /// silent default. Page numbers are 1-based and both paging inputs are clamped, so the response always
    /// says which page it actually returned.
    /// </summary>
    public async Task<CatalogDealPage> ListAsync(
        string category,
        int page,
        int pageSize,
        DealAlertContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var normalizedCategory = CatalogDealCategories.Parse(category);
        var normalizedPage = CatalogDealPager.NormalizePage(page);
        var normalizedPageSize = CatalogDealPager.NormalizePageSize(pageSize);
        var skip = (long)(normalizedPage - 1) * normalizedPageSize;

        var offers = repository.Get<GameOffer>();
        var steamGames = repository.Get<SteamGame>();

        // The tab counters are part of every response (spec L2: one envelope, no endpoint per count).
        var under50Total = await CountGamesAsync(offers, steamGames, context, CatalogDealCategories.Under50, cancellationToken);
        var historicTotal = await CountGamesAsync(offers, steamGames, context, CatalogDealCategories.Historic, cancellationToken);
        var scoreCandidates = await LoadScoreCandidatesAsync(offers, steamGames, context, cancellationToken);
        var scoreTotal = scoreCandidates.Count;

        int total;
        IReadOnlyList<long> pageGameIds;
        if (string.Equals(normalizedCategory, CatalogDealCategories.Score, StringComparison.Ordinal))
        {
            total = scoreTotal;
            pageGameIds = skip >= total
                ? []
                : scoreCandidates
                    .OrderBy(candidate => candidate.MinPriceMinor)
                    .ThenBy(candidate => candidate.GameId)
                    .Skip((int)skip)
                    .Take(normalizedPageSize)
                    .Select(candidate => candidate.GameId)
                    .ToList();
        }
        else
        {
            total = string.Equals(normalizedCategory, CatalogDealCategories.Under50, StringComparison.Ordinal)
                ? under50Total
                : historicTotal;
            pageGameIds = await PageGameIdsAsync(
                offers,
                steamGames,
                context,
                normalizedCategory,
                total,
                skip,
                normalizedPageSize,
                cancellationToken);
        }

        var items = await BuildItemsAsync(
            offers,
            steamGames,
            context,
            pageGameIds,
            normalizedCategory,
            cancellationToken);

        return new CatalogDealPage(
            normalizedCategory,
            items,
            items.Count,
            total,
            under50Total,
            historicTotal,
            scoreTotal,
            normalizedPage,
            normalizedPageSize,
            skip + items.Count < total);
    }

    /// <summary>How many distinct canonical games hold at least one qualifying offer in the category.</summary>
    private static Task<int> CountGamesAsync(
        IQueryable<GameOffer> offers,
        IQueryable<SteamGame> steamGames,
        DealAlertContext context,
        string category,
        CancellationToken cancellationToken) =>
        CatalogDealQueries
            .CategoryGameIds(CatalogDealQueries.Universe(offers, steamGames, context, category))
            .CountAsync(cancellationToken);

    /// <summary>
    /// The category page, resolved inside the database: cheapest qualifying offer per game, ordered by that
    /// price and then by game id, then <c>SKIP/TAKE</c>. Nothing outside the page is materialized.
    /// </summary>
    private static async Task<IReadOnlyList<long>> PageGameIdsAsync(
        IQueryable<GameOffer> offers,
        IQueryable<SteamGame> steamGames,
        DealAlertContext context,
        string category,
        int total,
        long skip,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (skip >= total)
        {
            return [];
        }

        var rows = await CatalogDealQueries
            .CategoryPage(CatalogDealQueries.Universe(offers, steamGames, context, category), skip, pageSize)
            .ToListAsync(cancellationToken);

        return rows.Select(row => row.GameId).ToList();
    }

    /// <summary>
    /// Candidate games of the score category, already evaluated by the shared formula.
    ///
    /// The SQL side is only the necessary condition (more than half off the game's MXN Steam base, which is
    /// what makes a positive discount factor possible); the exact rule is
    /// <see cref="DealAlertPolicy.DealScore"/> over the game's MXN Steam base and the provider-reported MXN
    /// history low — the same two inputs the notifier feeds it, read through the same exact
    /// <c>steam_game_id</c>. No foreign regular price is ever converted to stand in for the base, and a
    /// Steam-less game has no Steam snapshot, so it simply cannot score.
    /// </summary>
    private static async Task<IReadOnlyList<ScoreCandidate>> LoadScoreCandidatesAsync(
        IQueryable<GameOffer> offers,
        IQueryable<SteamGame> steamGames,
        DealAlertContext context,
        CancellationToken cancellationToken)
    {
        var mxnSnapshots = CatalogDealQueries.MxnSteamSnapshots(steamGames);

        var direct = (await CatalogDealQueries
                .ScoreSteamDirectCandidates(steamGames, context)
                .ToListAsync(cancellationToken))
            .Where(candidate => candidate.GameId != null &&
                candidate.BasePriceMinor != null &&
                candidate.MinPriceMinor != null)
            .Select(candidate => new ScoreCandidate(
                candidate.GameId!.Value,
                candidate.SteamGameId,
                candidate.BasePriceMinor!.Value,
                candidate.MinPriceMinor!.Value));

        var fromOffers = (await CatalogDealQueries
                .ScoreProviderCandidates(offers, mxnSnapshots, context)
                .ToListAsync(cancellationToken))
            .Where(candidate => candidate.GameId != null &&
                candidate.BasePriceMinor != null &&
                candidate.MinPriceMinor != null)
            .Select(candidate => new ScoreCandidate(
                candidate.GameId!.Value,
                candidate.SteamGameId,
                candidate.BasePriceMinor!.Value,
                candidate.MinPriceMinor!.Value));

        // One candidate per game. A game with more than one Steam snapshot keeps the cheapest one, then the
        // lower base, then the lower snapshot id: deterministic, and each candidate keeps its own base/low pair.
        var candidates = direct
            .Concat(fromOffers)
            .GroupBy(candidate => candidate.GameId)
            .Select(group => group
                .OrderBy(candidate => candidate.MinPriceMinor)
                .ThenBy(candidate => candidate.BasePriceMinor)
                .ThenBy(candidate => candidate.SteamGameId)
                .First())
            .ToList();

        var lows = await LoadHistoryLowsAsync(
            offers,
            candidates.Select(candidate => candidate.SteamGameId).Distinct().ToList(),
            cancellationToken);

        var scored = new List<ScoreCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var low = lows.TryGetValue(candidate.SteamGameId, out var value) ? value : (int?)null;
            var score = DealAlertPolicy.DealScore(
                candidate.BasePriceMinor,
                CatalogDealQuery.MxnCurrency,
                candidate.MinPriceMinor,
                low,
                low is null ? null : CatalogDealQuery.MxnCurrency,
                context.MinViableDiscountPercent);

            if (score is > DealAlertPolicy.ScoreTriggerThreshold)
            {
                scored.Add(candidate with { Score = score.Value });
            }
        }

        return scored;
    }

    /// <summary>
    /// Hydrates one page of games: the page's comparable rows, the canonical game rows, the Steam snapshots of
    /// those rows and the provider MXN history lows. Bounded by the page size, never by the catalog size.
    /// </summary>
    private async Task<IReadOnlyList<CatalogDealItem>> BuildItemsAsync(
        IQueryable<GameOffer> offers,
        IQueryable<SteamGame> steamGames,
        DealAlertContext context,
        IReadOnlyList<long> gameIds,
        string category,
        CancellationToken cancellationToken)
    {
        if (gameIds.Count == 0)
        {
            return [];
        }

        var rows = await CatalogDealQueries
            .RowsForGames(CatalogDealQueries.Universe(offers, steamGames, context), gameIds)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        // Steam context is keyed by the row's own steam_game_id: never the game's first snapshot, because a
        // second app id of the same game is a different price, a different base and a different dedup key.
        var steamGameIds = rows
            .Where(row => row.SteamGameId is not null)
            .Select(row => row.SteamGameId!.Value)
            .Distinct()
            .ToList();
        var steamById = steamGameIds.Count == 0
            ? new Dictionary<int, SteamGame>()
            : await steamGames
                .Where(game => steamGameIds.Contains(game.SteamGameId))
                .ToDictionaryAsync(game => game.SteamGameId, cancellationToken);

        var games = await repository
            .Get<Game>()
            .Where(game => gameIds.Contains(game.GameId))
            .ToListAsync(cancellationToken);
        var gamesById = games.ToDictionary(game => game.GameId);

        var lows = await LoadHistoryLowsAsync(offers, steamGameIds, cancellationToken);

        var decisions = new List<DealAlertDecision>(rows.Count);
        foreach (var row in rows)
        {
            gamesById.TryGetValue(row.GameId, out var game);
            steamById.TryGetValue(row.SteamGameId ?? -1, out var steam);
            var candidate = CatalogDealSelection.ToCandidate(
                row,
                steam,
                game,
                lows.TryGetValue(row.SteamGameId ?? -1, out var low) ? low : null);
            decisions.Add(DealAlertPolicy.Evaluate(candidate, context));
        }

        var items = new Dictionary<long, CatalogDealItem>();
        foreach (var decision in CatalogDealSelection.BestPerGame(decisions, category))
        {
            var gameId = decision.Candidate.GameId ?? 0;
            gamesById.TryGetValue(gameId, out var game);
            steamById.TryGetValue(decision.Candidate.SteamGameId ?? -1, out var steam);
            items[gameId] = CatalogDealSelection.ToItem(
                decision,
                decision.Candidate.GameName,
                game?.ImageUrl ?? steam?.ImageUrl,
                steam?.AppId);
        }

        // The page order comes from the database (or from the score list); hydration only attaches payloads.
        return gameIds
            .Where(items.ContainsKey)
            .Select(gameId => items[gameId])
            .ToList();
    }

    /// <summary>
    /// Provider-reported history lows in MXN keyed by the exact <c>steam_game_id</c> — the notifier's own query
    /// and key, so both surfaces feed the score formula the same number. A Steam-less game has no key here.
    /// </summary>
    private static async Task<Dictionary<int, int>> LoadHistoryLowsAsync(
        IQueryable<GameOffer> offers,
        IReadOnlyList<int> steamGameIds,
        CancellationToken cancellationToken)
    {
        if (steamGameIds.Count == 0)
        {
            return [];
        }

        var rows = await CatalogDealQueries.HistoryLows(offers, steamGameIds).ToListAsync(cancellationToken);

        var lows = new Dictionary<int, int>(rows.Count);
        foreach (var row in rows)
        {
            if (row.SteamGameId is { } steamGameId && row.Low is { } low)
            {
                lows[steamGameId] = low;
            }
        }

        return lows;
    }

    /// <summary>
    /// One game of the score category: the cheapest price that can qualify and the inputs the shared formula
    /// needs. <see cref="Score"/> is filled only when the shared policy accepted it.
    /// </summary>
    private sealed record ScoreCandidate(long GameId, int SteamGameId, int BasePriceMinor, int MinPriceMinor)
    {
        public double? Score { get; init; }
    }
}
