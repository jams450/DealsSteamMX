using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Read-only price-freshness monitor of the requesting user's wishlist (spec S4/S6): one page of games plus the
/// four coverage counters, answered entirely inside PostgreSQL.
///
/// What it reads: the canonical <c>games</c> rows of the wishlist of the authenticated caller (state
/// <c>wished</c>, resolved by <see cref="ICatalogWishlistScopeResolver"/>), the usable comparable MXN
/// <c>game_offers</c> rows of those games and their MX <c>steam_games</c> snapshots (for the Steam direct price
/// and the per-provider consultation stamps). The library, the favourites and every other account are outside
/// the query — not filtered out afterwards.
/// What it never does: call a provider, write anything, refresh anything, read a bundle, or touch identity.
///
/// The buckets are price <em>coverage</em> of the CHEAPEST usable comparable price — the one price the panel
/// shows and explains (user correction S9): `recent` means that price was observed inside
/// <see cref="CatalogFreshness.Hours"/>, `stale` means it is older, and `missing` means no usable comparable price
/// exists at all — which can be an unsupported game, an identity match the providers do not have, or simply no
/// answer yet. A newer and more expensive price of another shop never moves the game out of `stale`. Nothing here
/// claims a provider failed, and nothing claims a game was never attempted: the only evidence the row carries is
/// what persistence can prove.
///
/// The queries live in <see cref="CatalogFreshnessQueries"/> so the manual pending-price pass uses the same
/// universe and the offline checks can execute and render them. The caller supplies the reference instant
/// (<c>asOfUtc</c>) so the service owns no clock, exactly like <see cref="CatalogDealService"/>.
/// </summary>
public sealed class CatalogFreshnessService(
    IRepository repository,
    ICatalogWishlistScopeResolver wishlistScope)
{
    /// <summary>
    /// One page of the requested filter plus the counters of the whole wishlist. An unknown filter token throws
    /// <see cref="ArgumentException"/> (Spanish 400) instead of silently defaulting; paging inputs are clamped
    /// and the response reports the page it actually returned. A wishlist with no canonical game answers zeros
    /// without running a single aggregation, while still reporting the entries that could not be attributed.
    /// </summary>
    public async Task<CatalogFreshnessPage> ListAsync(
        int userId,
        string? status,
        int page,
        int pageSize,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var normalizedStatus = CatalogFreshnessStatus.Parse(status);
        var normalizedPage = CatalogDealPager.NormalizePage(page);
        var normalizedPageSize = CatalogDealPager.NormalizePageSize(pageSize);
        var skip = (long)(normalizedPage - 1) * normalizedPageSize;

        var scope = await wishlistScope.LoadAsync(userId, cancellationToken);
        if (scope.IsEmpty)
        {
            // No canonical game: nothing is counted, but the entries the scope could not attribute are still
            // reported — an unreadable wishlist must not look like a covered one.
            return new CatalogFreshnessPage(
                DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc),
                CatalogFreshness.Hours,
                normalizedPage,
                normalizedPageSize,
                0,
                new CatalogFreshnessCounts(0, 0, 0, 0),
                scope.UnresolvedEntries,
                []);
        }

        var prices = CatalogFreshnessQueries.PriceUniverse(
            repository.Get<GameOffer>(),
            repository.Get<SteamGame>(),
            asOfUtc);
        var states = CatalogFreshnessQueries.GameStates(
            CatalogFreshnessQueries.WishlistGames(repository.Get<Game>(), scope.GameIds),
            prices,
            asOfUtc);

        // The counters are computed over the whole wishlist and are part of every response, so the panel
        // paints all four filter badges with one round-trip and the filter never distorts them.
        var counts = CatalogFreshnessCounts.Of(
            await CatalogFreshnessQueries.Counts(states).ToListAsync(cancellationToken));

        var total = normalizedStatus switch
        {
            CatalogFreshnessStatus.Recent => counts.Recent,
            CatalogFreshnessStatus.Stale => counts.Stale,
            CatalogFreshnessStatus.Missing => counts.Missing,
            _ => counts.Total,
        };

        List<CatalogFreshnessGameState> pageStates = skip >= total
            ? []
            : await CatalogFreshnessQueries
                .Page(CatalogFreshnessQueries.Filter(states, normalizedStatus), skip, normalizedPageSize)
                .ToListAsync(cancellationToken);

        var items = await BuildItemsAsync(prices, pageStates, asOfUtc, cancellationToken);

        return new CatalogFreshnessPage(
            DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc),
            CatalogFreshness.Hours,
            normalizedPage,
            normalizedPageSize,
            total,
            counts,
            scope.UnresolvedEntries,
            items);
    }

    /// <summary>
    /// Hydrates one page: the canonical game rows, one MX Steam row per game (chosen by lowest
    /// <c>steam_game_id</c>) and the page's price rows. Three bounded queries for at most
    /// <see cref="CatalogDealPager.MaxPageSize"/> games, never one query per game.
    /// </summary>
    private async Task<IReadOnlyList<CatalogFreshnessItem>> BuildItemsAsync(
        IQueryable<CatalogFreshnessPriceRow> prices,
        IReadOnlyList<CatalogFreshnessGameState> states,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        if (states.Count == 0)
        {
            return [];
        }

        var gameIds = states.Select(state => state.GameId).ToList();

        var games = await repository.Get<Game>()
            .Where(game => gameIds.Contains(game.GameId))
            .Select(game => new { game.GameId, game.Title, game.ImageUrl })
            .ToListAsync(cancellationToken);
        var gamesById = games.ToDictionary(game => game.GameId);

        // A game may have more than one MX snapshot (two app ids). The lowest steam_game_id is the same
        // deterministic choice the pending-price pass makes, so the panel and the job talk about one row.
        var steamRows = await CatalogFreshnessQueries
            .SteamRowsForGames(repository.Get<SteamGame>(), gameIds)
            .ToListAsync(cancellationToken);
        var steamByGame = steamRows
            .GroupBy(row => row.GameId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => row.SteamGameId).First());

        var priceRows = await CatalogFreshnessQueries
            .PriceRowsForGames(prices, gameIds)
            .ToListAsync(cancellationToken);
        var rowsByGame = priceRows
            .GroupBy(row => row.GameId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var items = new List<CatalogFreshnessItem>(states.Count);
        foreach (var state in states)
        {
            gamesById.TryGetValue(state.GameId, out var game);
            steamByGame.TryGetValue(state.GameId, out var steam);

            items.Add(new CatalogFreshnessItem(
                state.GameId,
                game?.Title ?? string.Empty,
                game?.ImageUrl,
                steam?.AppId,
                state.Status,
                // The date, amount, shop, source and pricing type all come from the same chosen winner, so the
                // card can never show one row's price with another row's store name (user correction S9).
                Utc(state.ChosenObservedAt),
                state.ChosenPriceMinor,
                state.ChosenShopName,
                state.ChosenProvider,
                state.ChosenPricingType,
                CatalogFreshnessProviders.Build(
                    rowsByGame.TryGetValue(state.GameId, out var rows) ? rows : [],
                    steam,
                    asOfUtc)));
        }

        return items;
    }

    /// <summary>
    /// Npgsql already returns UTC kinds, but a timestamp read through another path may not: the contract says
    /// ISO UTC, so every instant is stamped as such instead of relying on the caller.
    /// </summary>
    private static DateTime? Utc(DateTime? value) =>
        value is { } instant ? DateTime.SpecifyKind(instant, DateTimeKind.Utc) : null;
}
