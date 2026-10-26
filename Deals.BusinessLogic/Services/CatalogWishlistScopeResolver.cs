using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Read-only source of <see cref="CatalogWishlistScope"/> (spec S4/S5). It reads two identity tables only —
/// <c>user_library</c> in state <c>wished</c> for one user, the <c>games</c> rows those entries claim, and the MX
/// <c>steam_games</c> snapshots an exact app id needs — and never a price, a provider, the library
/// (<c>owned</c>), the favourites or another account.
///
/// Three reads at most, all bounded by one person's own list: the wishlist rows, the claimed canonical ids that
/// really exist, and the app-id mappings of the rows that still need one. The whole catalog is never
/// materialized; the aggregation the monitor then runs is still SQL over
/// <c>games</c>/<c>game_offers</c>/<c>steam_games</c>, restricted by the ids this returns.
///
/// Entries that no exact path attributes are returned as a count — see <see cref="CatalogWishlistScope"/> — so a
/// wishlist the pass cannot read is reported instead of looking empty.
/// </summary>
public sealed class CatalogWishlistScopeResolver(IRepository repository) : ICatalogWishlistScopeResolver
{
    public async Task<CatalogWishlistScope> LoadAsync(int userId, CancellationToken cancellationToken = default)
    {
        // The gate is before the first query: an absent requester fails closed even when a caller forgot to
        // check, and there is no "account 1" to borrow.
        if (userId <= 0)
        {
            throw new ArgumentException(
                "La lista de precios pendientes solo se puede leer para el usuario autenticado que la pide.",
                nameof(userId));
        }

        var rows = await CatalogWishlistScopeQuery
            .WishedRows(repository.Get<UserLibrary>(), userId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return CatalogWishlistScope.Empty(userId);
        }

        // A canonical id is only an identity if the game exists; the schema does not enforce the reference.
        var claimed = rows
            .Where(row => row.GameId is > 0)
            .Select(row => row.GameId!.Value)
            .Distinct()
            .ToList();

        IReadOnlyList<long> existing = [];
        if (claimed.Count > 0)
        {
            existing = await CatalogWishlistScopeQuery
                .ExistingGameIds(repository.Get<Game>(), claimed)
                .ToListAsync(cancellationToken);
        }

        var known = new HashSet<long>(existing);

        // Only the rows without a usable canonical id need the exact Steam fallback.
        var needingFallback = rows
            .Where(row => row.GameId is not { } canonical || canonical <= 0 || !known.Contains(canonical))
            .ToList();
        var appIds = CatalogWishlistScopeQuery.SteamAppIds(needingFallback);

        var gameIdsByAppId = new Dictionary<int, IReadOnlyList<long>>();
        if (appIds.Count > 0)
        {
            var mappings = await CatalogWishlistScopeQuery
                .ResolvedGamesForAppIds(repository.Get<SteamGame>(), repository.Get<Game>(), appIds)
                .ToListAsync(cancellationToken);

            foreach (var mapping in mappings)
            {
                known.Add(mapping.GameId);
            }

            gameIdsByAppId = mappings
                .GroupBy(mapping => mapping.AppId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<long>)group.Select(mapping => mapping.GameId).Distinct().ToList());
        }

        var resolution = CatalogWishlistScopeQuery.Resolve(rows, known, gameIdsByAppId);

        return new CatalogWishlistScope(userId, resolution.GameIds, resolution.UnresolvedEntries);
    }
}
