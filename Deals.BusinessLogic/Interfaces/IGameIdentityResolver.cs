using Deals.BusinessLogic.Models.Catalog;
using Deals.Models.Entities;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Resolves — or creates — the canonical <see cref="Game"/> for a title plus one or more exact external
/// ids. Never matches or merges by title: only <c>(namespace, external_id)</c> identity joins rows.
/// </summary>
public interface IGameIdentityResolver
{
    /// <summary>
    /// Returns the canonical game owning every supplied id, creating and claiming a new row when none is
    /// known. Throws <see cref="InvalidOperationException"/> when the ids already map to different games.
    /// </summary>
    Task<Game> ResolveOrCreateGameAsync(GameIdentityRequest request, CancellationToken cancellationToken = default);
}
