namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// One IGDB game row offered by the manual-add search: art and identity data only. <c>SourceId</c> is
/// what the dialog sends back to fetch the cover server-side — never a URL from the client
/// (<c>docs/PLAN_CONSOLE.md</c> §5).
/// </summary>
public sealed record ManualSearchHit(
    long SourceId,
    string Title,
    int? ReleaseYear,
    string? ImageUrl,
    IReadOnlyList<ManualSearchPlatform> Platforms);

/// <summary>Console a search hit belongs to, as IGDB reports it (display only).</summary>
public sealed record ManualSearchPlatform(int Id, string Name);

/// <summary>
/// Outcome of a title search. <c>Source</c> is <c>"igdb"</c> when the provider answered and null when it
/// is unconfigured or failed — the dialog then reads "búsqueda no disponible" instead of "sin resultados".
/// </summary>
public sealed record ManualSearchResult(string? Source, IReadOnlyList<ManualSearchHit> Hits);

/// <summary>Cover and release data to apply to a game row this call creates. All fields nullable.</summary>
public sealed record ManualArtwork(string? Url, int? ReleaseYear);

/// <summary>
/// Outcome of reading the artwork of one provider row by id. <c>Source</c> null means the provider is
/// unavailable (unconfigured, auth failure, non-2xx) — never "not found"; a non-null <c>Source</c> with a
/// null <c>Artwork</c> (or a null <see cref="ManualArtwork.Url"/>) means the provider answered and has no
/// usable cover for that id. The two cases are distinct so a cover pick can map each to its own status
/// instead of collapsing them into one. Read-only, never writes and never returns a client-supplied URL.
/// </summary>
public sealed record ManualArtworkLookupResult(string? Source, ManualArtwork? Artwork);

/// <summary>
/// One provider row read by its id. <c>Source</c> null means the provider is unavailable (unconfigured,
/// auth failure, non-2xx) — never "not found"; a non-null <c>Source</c> with a null <c>Game</c> means the
/// provider answered and has no such row. Both cases are distinct so the caller can map each to its own
/// status instead of collapsing them into one. Read-only, never writes.
/// </summary>
public sealed record ManualSearchLookupResult(string? Source, ManualSearchHit? Game);
