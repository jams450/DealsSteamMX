using System.Text.Json;
using Deals.BusinessLogic.Exceptions;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Catalog;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Manual canonical merge. Identity is only the exact <c>(namespace, external_id)</c> mapping, so this
/// service never guesses a duplicate: <see cref="FindDuplicateGroupsAsync"/> suggests, a human decides,
/// and <see cref="MergeAsync"/> repoints every referrer of the absorbed game to the survivor in one
/// transaction before deleting it. Everything runs as raw SQL (no tracked entities): the order of the
/// statements is the correctness argument, not the change tracker's.
///
/// Referrer order is mandatory: <c>user_game_favorites</c> carries a composite primary key that would
/// raise 23505 on a collision and a CASCADE foreign key that would delete the rows outright, while
/// <c>steam_games</c> and <c>user_library</c> are NO ACTION and would raise 23503.
/// </summary>
public sealed class GameMergeService : IGameMergeService
{
    // Global identity lock. A merge and the import both need the whole identity space stable while they
    // run; a single advisory lock is the lazy correct guard.
    // ponytail: global lock, fragment per user if concurrent multi-user imports ever matter (identity
    // creation would still need the global one).
    private const string IdentityLockSql = "SELECT pg_advisory_xact_lock(hashtext('dealext.game_identity'))";

    private readonly IRepository _repository;

    public GameMergeService(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicateGroupsAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return [];
        }

        // The fold is intentionally NOT games.normalized_title: that key strips trailing edition tokens and
        // would re-expose false groups ("Mafia" vs "Mafia: Definitive Edition"). The apostrophe pass is
        // mandatory — GameTitleNormalizer drops apostrophes while the backfill's SQL fold turns them into a
        // space, so without it "assassin s creed" never matches "assassins creed".
        var groups = await _repository.SqlQueryAsync<GroupRow>(
            """
            SELECT f.folded AS "Folded",
                   array_to_string(array_agg(DISTINCT f.game_id), ',') AS "GameIds",
                   count(DISTINCT e.external_id) FILTER (WHERE e.namespace = 'steam')::int AS "SteamAppIdCount"
            FROM (
                SELECT g.game_id, ul.store,
                       btrim(regexp_replace(regexp_replace(
                           translate(lower(g.title), 'áàâäãåéèêëíìîïóòôöõúùûüñç', 'aaaaaaeeeeiiiiooooouuuunc'),
                           '[‘’ʼ'']', '', 'g'), '[^a-z0-9]+', ' ', 'g')) AS folded
                FROM public.games g
                JOIN public.user_library ul ON ul.game_id = g.game_id
                WHERE ul.user_id = {0} AND ul.state IN ('owned','subscription')
            ) f
            LEFT JOIN public.game_external_ids e ON e.game_id = f.game_id
            GROUP BY f.folded
            HAVING count(DISTINCT f.game_id) > 1
            ORDER BY f.folded
            """,
            userId);

        if (groups.Count == 0)
        {
            return [];
        }

        var gameIds = groups
            .SelectMany(group => ParseGameIds(group.GameIds))
            .Distinct()
            .ToList();

        // One batch query for every member of every group: no per-game lookup.
        var details = await _repository.SqlQueryAsync<MemberRow>(
            """
            SELECT g.game_id AS "GameId",
                   g.title   AS "Title",
                   e.external_id  AS "SteamAppId",
                   ul.store       AS "Store",
                   ul.store_game_id AS "StoreGameId"
            FROM public.games g
            LEFT JOIN public.game_external_ids e
                ON e.game_id = g.game_id AND e.namespace = 'steam'
            LEFT JOIN public.user_library ul
                ON ul.game_id = g.game_id AND ul.user_id = {0} AND ul.state IN ('owned','subscription')
            WHERE g.game_id = ANY(string_to_array({1}, ',')::bigint[])
            ORDER BY g.game_id
            """,
            userId,
            string.Join(',', gameIds));

        var rowsByGameId = details
            .GroupBy(row => row.GameId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var result = new List<DuplicateGroup>(groups.Count);
        foreach (var group in groups)
        {
            var memberIds = ParseGameIds(group.GameIds);
            var members = new List<DuplicateMember>(memberIds.Length);
            foreach (var gameId in memberIds.OrderBy(id => id))
            {
                rowsByGameId.TryGetValue(gameId, out var rows);
                var title = rows?.FirstOrDefault()?.Title ?? string.Empty;

                var stores = (rows ?? [])
                    .Where(row => !string.IsNullOrWhiteSpace(row.Store))
                    .Select(row => new DuplicateStoreRef(row.Store!, row.StoreGameId!))
                    .Distinct()
                    .ToList();

                var steamAppIds = (rows ?? [])
                    .Select(row => row.SteamAppId)
                    .Where(appId => !string.IsNullOrWhiteSpace(appId))
                    .Select(appId => appId!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                // A game holding more than one Steam appid cannot be merged: it would make the price
                // binding ambiguous on the survivor.
                var memberBlocked = steamAppIds.Count > 1;
                members.Add(new DuplicateMember(
                    gameId,
                    title,
                    stores,
                    steamAppIds,
                    memberBlocked,
                    memberBlocked ? "El juego canónico tiene más de un appid de Steam." : null));
            }

            var blocked = group.SteamAppIdCount > 1 || members.Any(member => member.Blocked);
            result.Add(new DuplicateGroup(
                group.Folded,
                members,
                blocked));
        }

        return result;
    }

    public async Task<GameMergeOutcome> MergeAsync(
        long absorbedGameId,
        long survivorGameId,
        int actorUserId,
        string actorName,
        CancellationToken cancellationToken = default)
    {
        // actorUserId is part of the contract for audit callers; the log stores the human name only.
        _ = actorUserId;

        if (absorbedGameId <= 0 || survivorGameId <= 0)
        {
            throw new ArgumentException("Los identificadores de juego deben ser positivos.", nameof(absorbedGameId));
        }

        if (absorbedGameId == survivorGameId)
        {
            throw new ArgumentException("No se puede fusionar un juego consigo mismo.", nameof(survivorGameId));
        }

        return await _repository.ExecuteInTransactionAsync(async () =>
        {
            // 0. Serialize every identity operation (merge and import) for the life of the transaction.
            await _repository.ExecuteSqlRawAsync(IdentityLockSql);

            // 1. Lock both rows in ascending id order: a deterministic order is what avoids deadlocks
            //    between two concurrent merges of the same pair.
            var locked = await _repository.SqlQueryAsync<GameIdRow>(
                "SELECT game_id AS \"GameId\" FROM public.games WHERE game_id IN ({0},{1}) ORDER BY game_id FOR UPDATE",
                absorbedGameId,
                survivorGameId);

            if (locked.Count != 2)
            {
                throw new GameNotFoundException(
                    locked.Count == 0 ? absorbedGameId : survivorGameId);
            }

            // 3. One canonical game can own at most one ('steam', appid): two different appids across the
            //    pair would make the survivor's price binding ambiguous.
            var steamIds = await _repository.SqlQueryAsync<ExternalIdRow>(
                """
                SELECT DISTINCT e.external_id AS "ExternalId"
                FROM public.game_external_ids e
                WHERE e.game_id IN ({0},{1}) AND e.namespace = 'steam'
                """,
                absorbedGameId,
                survivorGameId);

            if (steamIds.Count > 1)
            {
                return GameMergeOutcome.Blocked(
                    "Los dos juegos tienen appids de Steam distintos; no se pueden fusionar sin dejar el precio ambiguo.");
            }

            // 4. Reviews always move: game_reviews has no unique key on (user_id, game_id, platform) any
            //    more, so a review of each game on the same platform is simply two reviews of the
            //    survivor. Nothing is dropped and therefore nothing blocks the merge.

            // Snapshot BEFORE any write: only what is destroyed and cannot be rebuilt — the absorbed games
            // row and its external ids. Repointed rows (steam_games, user_library, game_reviews,
            // user_game_favorites) are not copied: they still exist.
            var snapshot = await CaptureSnapshotAsync(absorbedGameId);

            // 5.-9. Repoint every referrer, then delete the absorbed row. Order matters: steam_games and
            //        user_library are NO ACTION and game_reviews.game_id is CASCADE.
            var movedExternalIds = await _repository.ExecuteSqlRawAsync(
                "UPDATE public.game_external_ids SET game_id = {0}, updated_at = NOW() WHERE game_id = {1}",
                survivorGameId,
                absorbedGameId);

            var movedSteamGames = await _repository.ExecuteSqlRawAsync(
                "UPDATE public.steam_games SET game_id = {0}, updated_at = NOW() WHERE game_id = {1}",
                survivorGameId,
                absorbedGameId);

            var movedLibraryRows = await _repository.ExecuteSqlRawAsync(
                "UPDATE public.user_library SET game_id = {0}, updated_at = NOW() WHERE game_id = {1}",
                survivorGameId,
                absorbedGameId);

            await _repository.ExecuteSqlRawAsync(
                "UPDATE public.game_reviews SET game_id = {0}, updated_at = NOW() WHERE game_id = {1}",
                survivorGameId,
                absorbedGameId);

            // Favorites have a composite primary key (user_id, game_id) and a CASCADE foreign key, so the
            // collision is deleted first (the surviving row already means "favorite") and then the rest is
            // repointed. Deleting the absorbed games row before this would take the rows with it.
            await _repository.ExecuteSqlRawAsync(
                """
                DELETE FROM public.user_game_favorites a
                USING public.user_game_favorites b
                WHERE a.game_id = {1} AND b.game_id = {0} AND b.user_id = a.user_id
                """,
                survivorGameId,
                absorbedGameId);

            await _repository.ExecuteSqlRawAsync(
                "UPDATE public.user_game_favorites SET game_id = {0}, updated_at = NOW() WHERE game_id = {1}",
                survivorGameId,
                absorbedGameId);

            // 10. The log is the only record of the merge: no FK, because the absorbed row is deleted next.
            var mergedBy = string.IsNullOrWhiteSpace(actorName)
                ? null
                : actorName.Trim()[..Math.Min(actorName.Trim().Length, 100)];

            await _repository.ExecuteSqlRawAsync(
                """
                INSERT INTO public.game_merges
                    (survivor_game_id, absorbed_game_id, absorbed_snapshot, moved_external_ids,
                     moved_steam_games, moved_library_rows, dropped_reviews, merged_at, merged_by)
                VALUES ({0}, {1}, {2}::jsonb, {3}, {4}, {5}, {6}, NOW(), {7})
                """,
                survivorGameId,
                absorbedGameId,
                snapshot,
                movedExternalIds,
                movedSteamGames,
                movedLibraryRows,
                0,
                (object?)mergedBy ?? DBNull.Value);

            // 11. Both NO ACTION referrers are already repointed, so this cannot raise 23503.
            await _repository.ExecuteSqlRawAsync(
                "DELETE FROM public.games WHERE game_id = {0}",
                absorbedGameId);

            return new GameMergeOutcome(
                true,
                null,
                movedExternalIds,
                movedSteamGames,
                movedLibraryRows);
        });
    }

    /// <summary>
    /// Full JSONB snapshot of the absorbed game's destroyed rows. <c>to_jsonb</c> keeps every column, so
    /// the snapshot cannot silently lose one when the schema grows.
    /// </summary>
    private async Task<string> CaptureSnapshotAsync(long absorbedGameId)
    {
        var rows = await _repository.SqlQueryAsync<SnapshotRow>(
            """
            SELECT jsonb_build_object(
                'game', (SELECT to_jsonb(g) FROM public.games g WHERE g.game_id = {0}),
                'external_ids', COALESCE((
                    SELECT jsonb_agg(to_jsonb(e) ORDER BY e.game_external_id)
                    FROM public.game_external_ids e WHERE e.game_id = {0}), '[]'::jsonb)
            )::text AS "Snapshot"
            """,
            absorbedGameId);

        return rows.Count == 0 ? "{}" : rows[0].Snapshot;
    }

    /// <summary>Parses the comma-joined id list produced by <c>array_to_string(array_agg(...))</c>.</summary>
    private static long[] ParseGameIds(string joined) =>
        joined
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => long.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

    // SqlQueryRaw maps columns to these properties by name; the aliases above exist for that reason.
    // These are unmapped types: every property is a settable scalar, and parsing lives outside them.
    private sealed class GroupRow
    {
        public string Folded { get; set; } = string.Empty;

        // Comma-joined in SQL on purpose: no array/UUID mapping risk, parsed without a dependency.
        public string GameIds { get; set; } = string.Empty;

        public int SteamAppIdCount { get; set; }
    }

    private sealed class MemberRow
    {
        public long GameId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? SteamAppId { get; set; }
        public string? Store { get; set; }
        public string? StoreGameId { get; set; }
    }

    private sealed class GameIdRow
    {
        public long GameId { get; set; }
    }

    private sealed class ExternalIdRow
    {
        public string ExternalId { get; set; } = string.Empty;
    }

    private sealed class SnapshotRow
    {
        public string Snapshot { get; set; } = string.Empty;
    }
}
