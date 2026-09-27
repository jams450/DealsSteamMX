using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Composes detail annotations from one canonical identity lookup. Queries remain sequential because the
/// scoped repository owns one DbContext; this avoids unsafe parallel EF operations while removing the three
/// duplicate Steam AppID resolutions performed by the individual services.
/// </summary>
public sealed class SteamGameUserStateService(IRepository repository) : ISteamGameUserStateService
{
    private const string SteamNamespace = "steam";

    public async Task<SteamGameUserState> GetAsync(int userId, int appId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || appId <= 0)
        {
            return SteamGameUserState.Empty;
        }

        var appIdText = appId.ToString(CultureInfo.InvariantCulture);
        var canonical = await (
                from externalId in repository.Get<GameExternalId>()
                join game in repository.Get<Game>() on externalId.GameId equals game.GameId
                where externalId.NamespaceName == SteamNamespace && externalId.ExternalId == appIdText
                select new { game.GameId, game.NormalizedTitle })
            .FirstOrDefaultAsync(cancellationToken);

        if (canonical is null)
        {
            return SteamGameUserState.Empty;
        }

        var linkedRows = await repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId &&
                row.GameId == canonical.GameId &&
                (row.State == LibraryStates.Owned || row.State == LibraryStates.Subscription))
            .Select(row => new { row.Store, row.State })
            .ToListAsync(cancellationToken);

        var ownedStores = linkedRows
            .Where(row => row.State == LibraryStates.Owned && row.Store != SteamNamespace)
            .Select(row => row.Store)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(store => store, StringComparer.Ordinal)
            .ToList();
        var hasGamePass = linkedRows.Any(row => row.State == LibraryStates.Subscription);

        var reviews = await repository.Get<GameReview>()
            .Where(review => review.UserId == userId && review.GameId == canonical.GameId)
            .OrderByDescending(review => review.FinishedMonth)
            .ThenByDescending(review => review.StartedMonth)
            .ThenByDescending(review => review.GameReviewId)
            .ToListAsync(cancellationToken);
        var isFavorite = await repository.Get<UserGameFavorite>()
            .AnyAsync(favorite => favorite.UserId == userId && favorite.GameId == canonical.GameId, cancellationToken);

        var possibleMatchStores = new List<string>();
        if (ownedStores.Count == 0 && !hasGamePass && !string.IsNullOrWhiteSpace(canonical.NormalizedTitle))
        {
            var steamAppIds = await (
                    from game in repository.Get<Game>()
                    join externalId in repository.Get<GameExternalId>() on game.GameId equals externalId.GameId
                    where game.NormalizedTitle == canonical.NormalizedTitle && externalId.NamespaceName == SteamNamespace
                    select externalId.ExternalId)
                .Distinct()
                .ToListAsync(cancellationToken);

            var hasSingleSteamAppId = steamAppIds
                .Select(ParseAppId)
                .Where(value => value > 0)
                .Distinct()
                .Take(2)
                .Count() == 1;
            if (hasSingleSteamAppId)
            {
                var otherOwnedRows = await repository.Get<UserLibrary>()
                    .Where(row => row.UserId == userId && row.State == LibraryStates.Owned &&
                        row.Store != SteamNamespace && (row.GameId == null || row.GameId != canonical.GameId))
                    .Select(row => new { row.Store, row.Title })
                    .ToListAsync(cancellationToken);
                possibleMatchStores = otherOwnedRows
                    .Where(row => string.Equals(
                        GameTitleNormalizer.Normalize(row.Title),
                        canonical.NormalizedTitle,
                        StringComparison.Ordinal))
                    .Select(row => row.Store)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(store => store, StringComparer.Ordinal)
                    .ToList();
            }
        }

        return new SteamGameUserState(
            new GameOwnership(ownedStores, hasGamePass, possibleMatchStores),
            reviews,
            isFavorite);
    }

    private static int ParseAppId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
}
