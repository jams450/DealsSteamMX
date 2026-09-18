using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Ownership resolution for one game detail: exact ownership on another store, a Game Pass subscription
/// entry, and a single normalized-title candidate. Strictly a query: it never creates canonical games,
/// never inserts external ids and never writes <c>game_id</c>.
/// </summary>
public class GameOwnershipService : IGameOwnershipService
{
    private const string SteamNamespace = "steam";

    private readonly IRepository _repository;

    public GameOwnershipService(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<GameOwnership> ResolveAsync(
        int appId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (appId <= 0 || userId <= 0)
        {
            return GameOwnership.None;
        }

        var appIdText = appId.ToString(CultureInfo.InvariantCulture);

        // Query 1: exact ('steam', appid) -> canonical gameId plus its normalized title, one round trip.
        var canonical = await (
                from externalId in _repository.Get<GameExternalId>()
                join game in _repository.Get<Game>() on externalId.GameId equals game.GameId
                where externalId.NamespaceName == SteamNamespace && externalId.ExternalId == appIdText
                select new { game.GameId, game.NormalizedTitle })
            .FirstOrDefaultAsync(cancellationToken);

        if (canonical is null)
        {
            return GameOwnership.None;
        }

        // Query 2: every owned/subscription row of this user already linked to the canonical game.
        var linkedRows = await _repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId &&
                row.GameId == canonical.GameId &&
                (row.State == LibraryStates.Owned || row.State == LibraryStates.Subscription))
            .Select(row => new { row.Store, row.State })
            .ToListAsync(cancellationToken);

        // A subscription is never ownership, so only 'owned' rows feed ownedStores.
        var ownedStores = linkedRows
            .Where(row => row.State == LibraryStates.Owned && row.Store != SteamNamespace)
            .Select(row => row.Store)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(store => store, StringComparer.Ordinal)
            .ToList();

        var hasGamePass = linkedRows.Any(row => row.State == LibraryStates.Subscription);

        // Confirmed ownership or Game Pass is already the answer: no title candidate is computed.
        if (ownedStores.Count > 0 || hasGamePass)
        {
            return new GameOwnership(ownedStores, hasGamePass, []);
        }

        var normalizedTitle = canonical.NormalizedTitle;
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return new GameOwnership(ownedStores, hasGamePass, []);
        }

        // Query 3: the same uniqueness guard Fase 3 uses. The title must map to exactly one Steam appid.
        var steamAppIds = await (
                from game in _repository.Get<Game>()
                join externalId in _repository.Get<GameExternalId>() on game.GameId equals externalId.GameId
                where game.NormalizedTitle == normalizedTitle && externalId.NamespaceName == SteamNamespace
                select externalId.ExternalId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var distinctAppIds = steamAppIds
            .Select(ParseAppId)
            .Where(parsed => parsed > 0)
            .Distinct()
            .Count();

        if (distinctAppIds != 1)
        {
            return new GameOwnership(ownedStores, hasGamePass, []);
        }

        // Query 4: the user's other owned rows, normalized in memory because GameTitleNormalizer is not
        // translatable to SQL. Bounded by the user's own library, so the query count stays fixed.
        var otherOwnedRows = await _repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId &&
                row.State == LibraryStates.Owned &&
                row.Store != SteamNamespace &&
                (row.GameId == null || row.GameId != canonical.GameId))
            .Select(row => new { row.Store, row.Title })
            .ToListAsync(cancellationToken);

        var possibleMatchStores = otherOwnedRows
            .Where(row => string.Equals(
                GameTitleNormalizer.Normalize(row.Title),
                normalizedTitle,
                StringComparison.Ordinal))
            .Select(row => row.Store)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(store => store, StringComparer.Ordinal)
            .ToList();

        return new GameOwnership(ownedStores, hasGamePass, possibleMatchStores);
    }

    private static int ParseAppId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
}
