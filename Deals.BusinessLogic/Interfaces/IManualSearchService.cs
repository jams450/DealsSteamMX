using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Provider-backed title search and cover lookup for the manual library add
/// (<c>docs/PLAN_CONSOLE.md</c> §2.3/§5): IGDB, with credentials kept in the environment
/// (<c>IGDB_CLIENT_ID</c> plus <c>IGDB_TOKEN</c> or the <c>IGDB_CLIENT_SECRET</c> exchange).
/// Every failure degrades to "unavailable", never to an exception: the manual add never waits on a source.
/// </summary>
public interface IManualSearchService
{
    /// <summary>Bounded title search for the dialog. <c>Source == null</c> means unavailable, not "no hits".</summary>
    Task<ManualSearchResult> SearchAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>Cover plus release year for one provider row, or null when nothing usable comes back.</summary>
    Task<ManualArtwork?> ArtworkAsync(long sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same read as <see cref="ArtworkAsync"/> but keeps the two empty outcomes apart, exactly like
    /// <see cref="GameByIdAsync"/>: <c>Source</c> null means the provider is unavailable (the caller can
    /// retry later) and a non-null <c>Source</c> without a usable URL means the provider has no cover for
    /// that id. Never throws for a provider failure and never writes.
    /// </summary>
    Task<ManualArtworkLookupResult> ArtworkLookupAsync(long sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One provider row read by id, never by title: the caller asserts identity with the id and the title
    /// comes back from the provider. <c>Source</c> null means unavailable and a non-null <c>Source</c> with
    /// a null <c>Game</c> means the provider has no such row — two outcomes the caller reports separately.
    /// </summary>
    Task<ManualSearchLookupResult> GameByIdAsync(long sourceId, CancellationToken cancellationToken = default);
}
