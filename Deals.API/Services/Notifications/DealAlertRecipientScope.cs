using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Library;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Deals.API.Services.Notifications;

/// <summary>
/// The personal operator's interest set (spec S4): the union of the games the configured account marked as a
/// favorite and the games on its Steam wishlist, expressed in the two identities a persisted offer can carry
/// (<c>game_id</c> and <c>steam_game_id</c>).
///
/// It is a value object on purpose, but it is a <b>snapshot of one read</b>, not a cycle-long permission: the
/// dispatcher re-reads the scope from <see cref="IDealAlertRecipientScope"/> immediately before every send and
/// requires the same <see cref="UserId"/> plus membership, so an interest removed or an account deactivated while
/// the cycle runs is observed instead of trusted from the cycle's first read.
/// </summary>
public sealed record DealAlertRecipient(int UserId, IReadOnlySet<long> GameIds, IReadOnlySet<int> SteamGameIds)
{
    /// <summary>
    /// True when the offer identity is inside the operator's interests. A row with no identity at all never
    /// matches: the old global backlog may predate the canonical ids, and an unknown game cannot be attributed
    /// to the account.
    /// </summary>
    public bool Includes(long? gameId, int? steamGameId) =>
        (gameId is { } canonical && GameIds.Contains(canonical)) ||
        (steamGameId is { } steam && SteamGameIds.Contains(steam));
}

/// <summary>
/// Resolves the configured notification operator and its interest set as of this read. It is a required
/// constructor dependency of both phases, so there is no unscoped path, and the checks can drive it without a
/// database. The dispatcher calls it again for every claimed row just before the send.
/// </summary>
public interface IDealAlertRecipientScope
{
    /// <summary>
    /// Null means "fail closed": no operator configured (<see cref="NtfyOptions.UserId"/> &lt;= 0), the account
    /// does not exist, or it is inactive. The pipeline must not produce, claim or send in that state.
    /// </summary>
    Task<DealAlertRecipient?> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Read-only SQL source of <see cref="DealAlertRecipient"/>. It only reads identity tables
/// (<c>users</c>, <c>user_game_favorites</c>, <c>user_library</c>, <c>steam_games</c>): no price, no provider,
/// no write. The user id is never inferred from an admin or from the caller of an endpoint; it comes from
/// <see cref="NtfyOptions.UserId"/> and nowhere else (spec S4, L3).
///
/// The first gate is deliberately before any query, so a missing id fails closed even when the host never ran
/// the options validation (a check or a manual cycle that constructs the pipeline directly).
/// </summary>
public sealed class DealAlertRecipientScope(
    IRepository repository,
    IOptions<NtfyOptions> options) : IDealAlertRecipientScope
{
    /// <summary>The only region the wishlist pipeline writes, and the one the price snapshots use.</summary>
    private const string SteamRegion = "mx";

    public async Task<DealAlertRecipient?> LoadAsync(CancellationToken cancellationToken)
    {
        var userId = options.Value.UserId;
        if (userId <= 0)
        {
            return null;
        }

        var user = await repository.Get<User>()
            .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (user is null || !user.Active)
        {
            return null;
        }

        var gameIds = new HashSet<long>();
        var favoriteIds = await repository.Get<UserGameFavorite>()
            .Where(favorite => favorite.UserId == userId)
            .Select(favorite => favorite.GameId)
            .ToListAsync(cancellationToken);
        gameIds.UnionWith(favoriteIds);

        var wished = await repository.Get<UserLibrary>()
            .Where(row => row.UserId == userId && row.State == LibraryStates.Wished)
            .Select(row => new { row.GameId, row.Store, row.StoreGameId })
            .ToListAsync(cancellationToken);

        var appIds = new HashSet<int>();
        foreach (var row in wished)
        {
            if (row.GameId is { } canonical)
            {
                gameIds.Add(canonical);
            }

            // A wishlist row may lack the canonical id: its Steam appid is the only identity it shares with
            // the price snapshots, so it is resolved exactly, never by title.
            if (string.Equals(row.Store, StoreKeys.Steam, StringComparison.Ordinal))
            {
                var appId = ParseAppId(row.StoreGameId);
                if (appId > 0)
                {
                    appIds.Add(appId);
                }
            }
        }

        var steamGameIds = new HashSet<int>();
        if (appIds.Count > 0)
        {
            var mapped = await repository.Get<SteamGame>()
                .Where(game => game.Region == SteamRegion && appIds.Contains(game.AppId))
                .Select(game => new { game.SteamGameId, game.GameId })
                .ToListAsync(cancellationToken);
            foreach (var row in mapped)
            {
                steamGameIds.Add(row.SteamGameId);
                if (row.GameId is { } canonical)
                {
                    gameIds.Add(canonical);
                }
            }
        }

        // An offer of a wanted canonical game may hang off its steam_game_id only (no game_id on the row), so
        // the canonical ids are expanded to the exact Steam snapshots they own.
        if (gameIds.Count > 0)
        {
            var linked = await repository.Get<SteamGame>()
                .Where(game => game.GameId != null && gameIds.Contains(game.GameId.Value))
                .Select(game => game.SteamGameId)
                .ToListAsync(cancellationToken);
            steamGameIds.UnionWith(linked);
        }

        return new DealAlertRecipient(userId, gameIds, steamGameIds);
    }

    private static int ParseAppId(string storeGameId) =>
        int.TryParse(storeGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) ? appId : 0;
}
