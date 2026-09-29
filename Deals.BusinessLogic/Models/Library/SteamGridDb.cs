namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Outcome of the SteamGridDB cover lookup, shaped exactly like <see cref="ManualArtworkLookupResult"/> so
/// the cover chain reads both providers the same way: <c>Source</c> null means the provider is unavailable
/// (no key configured, auth failure, non-2xx, malformed payload, transport error) — never "no artwork" — and
/// a non-null <c>Source</c> with a null <see cref="Url"/> means the provider answered and has no usable
/// cover for that title. The two cases stay apart because the pass reports them with different counters,
/// and collapsing them would turn an unconfigured provider into a silent "this game has no art anywhere".
/// Read-only: never writes, and the URL is always the one SteamGridDB returned.
/// </summary>
public sealed record SteamGridDbCoverLookupResult(string? Source, string? Url);

/// <summary>
/// One autocomplete candidate of a SteamGridDB title search: the game id the manual picker sends back, the
/// name exactly as the provider spells it, and whether the row is the game's own record (<c>verified</c>) or a
/// community alias. Display only: the autocomplete payload carries no artwork, so the URL is resolved later,
/// by id.
/// </summary>
public sealed record SteamGridDbGameCandidate(int Id, string Name, bool Verified);

/// <summary>
/// Outcome of a SteamGridDB title search. <c>Source</c> null means the provider could not be consulted (no
/// key, transport failure, malformed payload) — never "no game by that title" — and a non-null
/// <c>Source</c> with an empty <see cref="Candidates"/> means the provider answered and knows no game by
/// that title. The two are different answers and the picker shows them differently. Read-only: never writes.
/// </summary>
public sealed record SteamGridDbSearchResult(string? Source, IReadOnlyList<SteamGridDbGameCandidate> Candidates);
