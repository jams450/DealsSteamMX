using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Claims an exact external identifier for a canonical game in <c>game_external_ids</c>.
///
/// <para>
/// The rule lives here because two writers need it with different namespaces (Epic's urlSlug and Xbox's
/// StoreId) and a second copy is a second chance to get it wrong. Claiming is advisory: the caller decides
/// what to do when the id already belongs to someone else, and both callers drop their write rather than
/// price the wrong game.
/// </para>
/// </summary>
internal static class GameIdentityClaimer
{
    /// <summary>
    /// Inserts the mapping when it is free. Returns false when another canonical game already owns the pair,
    /// true when this game owns it or when there is no canonical row to attach it to (the caller still writes
    /// and the claim happens once the backfill gives the game a canonical row).
    /// </summary>
    public static async Task<bool> TryClaimAsync(
        IRepository repository,
        long? gameId,
        string namespaceName,
        string externalId,
        CancellationToken cancellationToken)
    {
        if (gameId is not long id)
        {
            return true;
        }

        var inserted = await repository.ExecuteSqlRawAsync(
            "INSERT INTO game_external_ids (game_id, namespace, external_id, created_at, updated_at) " +
            "VALUES ({0}, {1}, {2}, NOW(), NOW()) ON CONFLICT (namespace, external_id) DO NOTHING",
            id, namespaceName, externalId);

        if (inserted > 0)
        {
            return true;
        }

        var owner = await repository.Get<GameExternalId>()
            .Where(external => external.NamespaceName == namespaceName && external.ExternalId == externalId)
            .Select(external => (long?)external.GameId)
            .FirstOrDefaultAsync(cancellationToken);

        return owner == id;
    }
}
