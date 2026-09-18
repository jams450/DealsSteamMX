using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Catalog;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Atomic canonical resolver. Identity is only the unique mapping <c>(namespace, external_id)</c>; the
/// title is stored when a row is created but never used to match. Concurrent claims use the same
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> pattern as the external-bundle route; a loser re-reads the
/// canonical row instead of racing a unique key.
/// </summary>
public sealed class GameIdentityResolver(IRepository repository) : IGameIdentityResolver
{
    public async Task<Game> ResolveOrCreateGameAsync(
        GameIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A non-blank title is required.", nameof(request));
        }

        var ids = NormalizeIds(request.ExternalIds);
        if (ids.Count == 0)
        {
            throw new ArgumentException("At least one non-blank external id is required.", nameof(request));
        }

        return await repository.ExecuteInTransactionAsync(
            () => ResolveCoreAsync(title, ids, cancellationToken));
    }

    private async Task<Game> ResolveCoreAsync(
        string title,
        IReadOnlyList<GameExternalIdRef> ids,
        CancellationToken cancellationToken)
    {
        // 1. Read every supplied id. All of them, if known, must already agree on one canonical game.
        var existing = new List<GameExternalId>();
        foreach (var id in ids)
        {
            var row = await repository.Get<GameExternalId>()
                .FirstOrDefaultAsync(
                    candidate => candidate.NamespaceName == id.NamespaceName && candidate.ExternalId == id.ExternalId,
                    cancellationToken);
            if (row is not null)
            {
                existing.Add(row);
            }
        }

        var owners = existing.Select(row => row.GameId).Distinct().ToList();
        if (owners.Count > 1)
        {
            throw new InvalidOperationException(
                $"External ids map to multiple canonical games ({string.Join(", ", owners)}); refusing to merge.");
        }

        // 2. Own the canonical row: an existing one when a mapping resolved, a fresh one otherwise.
        Game game;
        var createdGame = false;
        if (owners.Count == 1)
        {
            game = await repository.GetTrack<Game>()
                .FirstAsync(candidate => candidate.GameId == owners[0], cancellationToken);
        }
        else
        {
            game = new Game
            {
                Title = title,
                NormalizedTitle = GameTitleNormalizer.Normalize(title)
            };
            repository.GetTrack<Game>().Add(game);
            await repository.SaveChangesAsync();
            createdGame = true;
        }

        // 3. Claim every missing mapping for this game. The unique key is the identity: a conflicting
        //    claim is a no-op, never an update of somebody else's row.
        var known = existing.Select(row => (row.NamespaceName, row.ExternalId)).ToHashSet();
        var conflicted = 0;
        foreach (var id in ids)
        {
            if (known.Contains((id.NamespaceName, id.ExternalId)))
            {
                continue;
            }

            var inserted = await repository.ExecuteSqlRawAsync(
                "INSERT INTO game_external_ids (game_id, namespace, external_id, created_at, updated_at) " +
                "VALUES ({0}, {1}, {2}, NOW(), NOW()) ON CONFLICT (namespace, external_id) DO NOTHING",
                game.GameId, id.NamespaceName, id.ExternalId);

            if (inserted == 0)
            {
                conflicted++;
            }
        }

        if (conflicted == 0)
        {
            return game;
        }

        // 4. A concurrent writer claimed at least one id first. Re-read all of them: they must resolve to
        //    a single owner, and we must not have claimed a different one.
        var claimedOwners = new List<long>(ids.Count);
        foreach (var id in ids)
        {
            claimedOwners.Add(await repository.Get<GameExternalId>()
                .Where(candidate => candidate.NamespaceName == id.NamespaceName && candidate.ExternalId == id.ExternalId)
                .Select(candidate => candidate.GameId)
                .SingleAsync(cancellationToken));
        }

        var distinctOwners = claimedOwners.Distinct().ToList();
        if (distinctOwners.Count > 1)
        {
            throw new InvalidOperationException(
                $"External ids map to multiple canonical games ({string.Join(", ", distinctOwners)}); refusing to merge.");
        }

        var owner = distinctOwners[0];
        if (owner == game.GameId)
        {
            return game;
        }

        if (!createdGame)
        {
            // The pre-existing game lost a mapping to another game: an actual identity conflict, not a
            // race we can absorb.
            throw new InvalidOperationException(
                $"External id resolved to canonical game {owner} but the supplied ids belong to {game.GameId}.");
        }

        // The fresh row lost every claim, so it holds no mappings: drop the orphan and adopt the winner.
        await repository.RemoveAsync(game);
        return await repository.GetTrack<Game>()
            .FirstAsync(candidate => candidate.GameId == owner, cancellationToken);
    }

    private static List<GameExternalIdRef> NormalizeIds(IReadOnlyList<GameExternalIdRef>? ids)
    {
        var normalized = new List<GameExternalIdRef>(ids?.Count ?? 0);
        if (ids is null)
        {
            return normalized;
        }

        var seen = new HashSet<(string Namespace, string ExternalId)>();
        foreach (var id in ids)
        {
            var ns = id.NamespaceName?.Trim().ToLowerInvariant();
            var externalId = id.ExternalId?.Trim();
            if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(externalId))
            {
                continue;
            }

            if (seen.Add((ns, externalId)))
            {
                normalized.Add(new GameExternalIdRef(ns, externalId));
            }
        }

        return normalized;
    }
}
