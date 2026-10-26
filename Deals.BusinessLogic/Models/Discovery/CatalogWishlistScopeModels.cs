using System.Globalization;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>
/// The price universe of one authenticated user (spec S4/S5): the canonical games of their
/// <c>user_library</c> rows in state <c>wished</c>, and nothing else.
///
/// It is a value object, not a permission and not a snapshot of a background cycle: the monitor and the manual
/// pass resolve it per request from the requester's own id, which the server took from the access token. There
/// is no configured account to fall back to and no width — the library (<c>owned</c>), the favourites and other
/// accounts are simply not part of the query.
///
/// <see cref="UnresolvedEntries"/> is the honest other half: wishlist <em>entries</em> (rows, not games) that no
/// exact identity path could attribute to an existing game. They cannot be monitored or priced, and hiding them
/// would make a wishlist of five unattributable rows look like an empty wishlist that is fully covered. The two
/// numbers are deliberately different units and must be reported apart:
/// <list type="bullet">
/// <item><see cref="GameIds"/> — canonical games, de-duplicated: two rows that resolve to the same game are one
/// game, because the counters and the batch are per game;</item>
/// <item><see cref="UnresolvedEntries"/> — rows: a row count, labelled «entradas» in the API, because two
/// unattributable rows are two things the person has to fix, not one.</item>
/// </list>
/// </summary>
public sealed record CatalogWishlistScope(int UserId, IReadOnlyList<long> GameIds, int UnresolvedEntries)
{
    /// <summary>A legitimate answer: the user has no wished row with a canonical game and none unresolved.</summary>
    public static CatalogWishlistScope Empty(int userId) => new(userId, [], 0);

    /// <summary>No canonical game: nothing to monitor and nothing to reprice.</summary>
    public bool IsEmpty => GameIds.Count == 0;
}

/// <summary>
/// One <c>user_library</c> row reduced to the three facts that decide whether it can be attributed to a
/// canonical game: its canonical id (optional), the store that wrote it and the store's own identifier.
/// </summary>
public sealed record CatalogWishlistRow
{
    public long? GameId { get; init; }

    public string Store { get; init; } = string.Empty;

    public string StoreGameId { get; init; } = string.Empty;
}

/// <summary>One exact app-id → canonical-game mapping that survived the existence check.</summary>
public sealed record CatalogWishlistSteamMapping
{
    public int AppId { get; init; }

    public long GameId { get; init; }
}

/// <summary>
/// The outcome of attributing every wished row: the canonical games (de-duplicated) and how many rows could not
/// be attributed at all. See <see cref="CatalogWishlistScope"/> for why the two units differ.
/// </summary>
public sealed record CatalogWishlistResolution(IReadOnlyList<long> GameIds, int UnresolvedEntries);

/// <summary>
/// Read-only queries and the one decision that turn one user's wishlist into canonical games plus a count of the
/// entries that cannot be attributed (spec S4/S5).
///
/// Two identity paths, both exact:
/// <list type="number">
/// <item>the row already carries <c>game_id</c> — and that game must really exist (the schema does not enforce
/// the reference, so an orphan id is not an identity);</item>
/// <item>the row is a Steam row whose <c>store_game_id</c> is a positive app id: it is resolved through
/// <c>steam_games</c> for the MX region, joined to <c>games</c> so the mapped game must exist too. This is also
/// the safe rescue for a row whose canonical id is orphaned. Never by title — <c>steam_games.name</c> is stored
/// localized, so a title match is a guess that silently prices somebody else's game.</item>
/// </list>
///
/// A row that neither path resolves is counted as an unresolved entry: it is reported, never invented, and never
/// allowed to look like coverage elsewhere. The builders are composable so the offline checks can execute the
/// decision over in-memory rows and still observe the SQL the production provider generates.
/// </summary>
public static class CatalogWishlistScopeQuery
{
    /// <summary>The only region the wishlist sync and the price snapshots use.</summary>
    public const string SteamRegion = "mx";

    /// <summary>
    /// The wishlist rows of one user, filtered in SQL by owner and state. A row present in <c>user_library</c>
    /// with state <c>wished</c> <em>is</em> the active wish: removing a wish deletes the row, so there is no
    /// soft-delete column to filter on. A non-positive user id is refused instead of being coerced into an
    /// account: there is no default owner for this surface.
    /// </summary>
    public static IQueryable<CatalogWishlistRow> WishedRows(IQueryable<UserLibrary> rows, int userId)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (userId <= 0)
        {
            throw new ArgumentException(
                "La lista de precios pendientes solo se puede leer para el usuario autenticado que la pide.",
                nameof(userId));
        }

        var wished = LibraryStates.Wished;

        return rows
            .Where(row => row.UserId == userId && row.State == wished)
            .Select(row => new CatalogWishlistRow
            {
                GameId = row.GameId,
                Store = row.Store,
                StoreGameId = row.StoreGameId,
            });
    }

    /// <summary>
    /// The exact Steam app id of one row, when the row is a Steam row whose store id really is a positive
    /// integer. Parsing happens in C# because <c>store_game_id</c> is text and a cast in SQL could fail at
    /// runtime on another store's key; the list is bounded by one user's own wishlist, not by the catalog.
    /// </summary>
    public static int? ExactSteamAppId(CatalogWishlistRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!string.Equals(row.Store, StoreKeys.Steam, StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(row.StoreGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) &&
            appId > 0
            ? appId
            : null;
    }

    /// <summary>The exact app ids of the given rows, de-duplicated and ordered.</summary>
    public static IReadOnlyList<int> SteamAppIds(IEnumerable<CatalogWishlistRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var appIds = new SortedSet<int>();
        foreach (var row in rows)
        {
            if (ExactSteamAppId(row) is { } appId)
            {
                appIds.Add(appId);
            }
        }

        return appIds.ToList();
    }

    /// <summary>
    /// The claimed canonical ids that really exist. This is the whole point of the check: <c>game_id</c> is a
    /// nullable reference the schema does not enforce, so a merge or a bad import can leave an id that points at
    /// nothing, and adopting it would fabricate a game.
    /// </summary>
    public static IQueryable<long> ExistingGameIds(IQueryable<Game> games, IReadOnlyCollection<long> gameIds)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(gameIds);

        if (gameIds.Count == 0)
        {
            return games.Where(game => false).Select(game => game.GameId);
        }

        return games
            .Where(game => gameIds.Contains(game.GameId))
            .Select(game => game.GameId);
    }

    /// <summary>
    /// The canonical games of those app ids, in SQL: MX snapshots only, joined to <c>games</c> so only existing
    /// games come back. An app id may resolve to more than one game — a merge can legitimately make two
    /// snapshots point at one game — and every one of them stays inside the wishlist scope.
    /// </summary>
    public static IQueryable<CatalogWishlistSteamMapping> ResolvedGamesForAppIds(
        IQueryable<SteamGame> steamGames,
        IQueryable<Game> games,
        IReadOnlyCollection<int> appIds)
    {
        ArgumentNullException.ThrowIfNull(steamGames);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(appIds);

        var region = SteamRegion;

        var snapshots = steamGames.Where(game =>
            game.Region == region && game.GameId != null && appIds.Contains(game.AppId));

        return from snapshot in snapshots
               join game in games on snapshot.GameId!.Value equals game.GameId
               select new CatalogWishlistSteamMapping
               {
                   AppId = snapshot.AppId,
                   GameId = snapshot.GameId!.Value,
               };
    }

    /// <summary>
    /// The one decision of the scope: for each row, a canonical id if it exists, else the exact Steam mapping if
    /// that resolves to an existing game, else "unresolved". Valid canonical ids always win over a Steam mapping,
    /// so a row that is already attributed is never re-attributed by a second path.
    ///
    /// <paramref name="existingGameIds"/> is the set of canonical ids already proven to exist (the claimed ones
    /// that were found, plus the ones the app-id join returned), which keeps every returned id a real game.
    /// </summary>
    public static CatalogWishlistResolution Resolve(
        IReadOnlyList<CatalogWishlistRow> rows,
        IReadOnlySet<long> existingGameIds,
        IReadOnlyDictionary<int, IReadOnlyList<long>> gameIdsByAppId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(existingGameIds);
        ArgumentNullException.ThrowIfNull(gameIdsByAppId);

        var gameIds = new SortedSet<long>();
        var unresolved = 0;

        foreach (var row in rows)
        {
            if (row.GameId is { } canonical && canonical > 0 && existingGameIds.Contains(canonical))
            {
                gameIds.Add(canonical);
                continue;
            }

            if (ExactSteamAppId(row) is { } appId &&
                gameIdsByAppId.TryGetValue(appId, out var mapped) &&
                mapped.Any(existingGameIds.Contains))
            {
                gameIds.UnionWith(mapped.Where(existingGameIds.Contains));
                continue;
            }

            // Nothing exact attributes this row: it is counted, never guessed.
            unresolved++;
        }

        return new CatalogWishlistResolution(gameIds.ToList(), unresolved);
    }
}
