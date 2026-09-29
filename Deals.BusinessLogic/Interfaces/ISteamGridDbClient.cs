using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// SteamGridDB artwork for one title: the last source of the automatic cover chain
/// (<c>docs/PLAN_LIBRARY.md</c> §9) and, unlike Steam and IGDB, a catalogue of community art rather than a
/// store catalogue — which is why it never proves identity and only ever contributes a display URL.
/// Credentials are read from the environment (<c>STEAMGRIDDB_API_KEY</c>) and never logged or returned.
/// Every failure degrades to "unavailable": the cover pass must never abort because one provider is down,
/// unconfigured or answering something that is not the documented shape.
/// </summary>
public interface ISteamGridDbClient
{
    /// <summary>
    /// Cover URL of the grid that best matches <paramref name="title"/>, or an empty answer. The lookup is
    /// two sequential requests (autocomplete, then the grids of the chosen game) and never retries: the
    /// provider documents no rate limit, so the pass stays sequential and bounded instead of guessing one.
    /// <c>Source</c> null means the provider could not be consulted; a non-null <c>Source</c> with a null
    /// <c>Url</c> means it answered without a usable grid. Never throws and never writes.
    /// </summary>
    Task<SteamGridDbCoverLookupResult> LookupCoverUrlAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Autocomplete candidates for <paramref name="title"/>, in one request. An empty or whitespace title
    /// answers <c>(steamgriddb, [])</c> before the key is even read: the caller gets today's answer for an
    /// empty term. No key answers <c>(null, [])</c>, and a non-empty <c>data</c> array where nothing parsed is
    /// <c>(null, [])</c> too — "unavailable", never "no game". The list is capped at <c>MaxCandidates</c> and
    /// the term truncated at <c>MaxTermLength</c>, exactly like <see cref="LookupCoverUrlAsync"/>. Never
    /// throws except the caller's own cancellation and never writes.
    /// </summary>
    Task<SteamGridDbSearchResult> SearchAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cover URL of the best-scoring 600x900 static grid of <paramref name="gameId"/>, in one request. Same
    /// dimensions, same grid cap and same best-<c>score</c> rule as the automatic chain. A non-positive id
    /// answers <c>(steamgriddb, null)</c> without spending a request; no key answers <c>(null, null)</c>, a
    /// not-success or malformed payload is <c>(null, null)</c> ("unavailable") and an answered game with no
    /// usable grid is <c>(steamgriddb, null)</c>. Never throws except the caller's own cancellation.
    /// </summary>
    Task<SteamGridDbCoverLookupResult> GetCoverUrlByIdAsync(int gameId, CancellationToken cancellationToken = default);
}
