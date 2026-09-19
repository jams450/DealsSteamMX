using System.Globalization;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// The single read-only <c>('steam', appid) → game_id</c> lookup. The ownership block, the reviews and the
/// favorites all ask the same question, so the query lives here once: an appid the catalog does not know
/// resolves to null and every caller treats that as "no identity" instead of creating one. Claiming
/// identity is <see cref="IGameIdentityResolver"/>'s job, not this one.
/// </summary>
internal static class SteamAppIdResolver
{
    private const string SteamNamespace = "steam";

    public static Task<long?> ResolveGameIdAsync(
        IRepository repository,
        int appId,
        CancellationToken cancellationToken = default)
    {
        if (appId <= 0)
        {
            return Task.FromResult<long?>(null);
        }

        var appIdText = appId.ToString(CultureInfo.InvariantCulture);

        return (
                from externalId in repository.Get<GameExternalId>()
                where externalId.NamespaceName == SteamNamespace && externalId.ExternalId == appIdText
                select (long?)externalId.GameId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
