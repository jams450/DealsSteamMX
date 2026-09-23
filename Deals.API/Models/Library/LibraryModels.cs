namespace Deals.API.Models.Library;

using Deals.API.Models.Reviews;
using Deals.BusinessLogic.Models.Library;

/// <summary>
/// One entry of a Playnite library export. Properties keep the exact JSON names the exporter emits;
/// extra fields are ignored by design so exports from newer Playnite builds keep working.
/// <see cref="Added"/> stays a string because Playnite serializes .NET dates as
/// <c>/Date(&lt;epoch-milliseconds&gt;)/</c>, which System.Text.Json cannot bind to <see cref="DateTime"/>.
/// </summary>
public sealed class PlayniteLibraryEntry
{
    public string? GameId { get; set; }
    public string? PluginId { get; set; }
    public string? Source { get; set; }
    public string? Name { get; set; }
    public bool? IsInstalled { get; set; }
    public string? Added { get; set; }
}

/// <summary>
/// Result of a Playnite import. <see cref="Unresolved"/> is always 0 in this phase (the canonical
/// catalog does not exist yet); <see cref="ByStore"/> counts accepted entries per canonical store.
/// </summary>
public sealed record LibraryImportResponse(
    int Imported,
    int Updated,
    int Unresolved,
    int UnsupportedSource,
    IReadOnlyDictionary<string, int> ByStore);

/// <summary>
/// One accepted library row. PluginId is never exposed. The trailing fields are the read-only price
/// binding: <c>PriceState</c> is <c>exact | title_candidate | none | subscription</c>, and the amounts
/// are MXN minor units (null when there is no converted price). No field implies ownership.
/// <see cref="Title"/> is the canonical <c>games.title</c> when the row has a canonical link, and the
/// imported <c>user_library.title</c> only when it does not: an admin edit of the canonical name shows up
/// here and a Playnite reimport cannot overwrite it.
/// <see cref="GameId"/> is the canonical link (null when the row has no identity) and <see cref="Review"/>
/// is the caller's most recently written review for this exact <c>(gameId, platform)</c>, or null when
/// there is none. A pair can hold several reviews (a replay is a new review): the row is a compact badge,
/// so it carries the newest and the drawer fetches the full list from <c>/api/reviews</c>.
/// <see cref="IsFavorite"/> is the user's mark on the canonical game (false for a row without identity) and
/// <see cref="PlayedYears"/> are every year the pair was played, newest first, derived from <em>all</em> its
/// reviews: the year filter and the per-year report read this, not only the newest review.
/// </summary>
public sealed record LibraryItemResponse(
    long UserLibraryId,
    string Store,
    string StoreGameId,
    string Title,
    string State,
    bool? IsInstalled,
    DateTime? AddedAt,
    DateTime ImportedAt,
    string PriceState,
    string? BindingSource,
    int? SteamAppId,
    int? BestOfficialMinor,
    int? BestKeyshopMinor,
    int? HistoryLowMinor,
    int? BasePriceMinor,
    string? BaseCurrency,
    long? GameId,
    ReviewResponse? Review,
    string? ImageUrl,
    bool IsFavorite,
    IReadOnlyList<int> PlayedYears);

/// <summary>
/// Body of a cover sync pass. <see cref="Limit"/> is optional: the service applies its own default and
/// rejects a value outside 1..<c>LibraryCoverService.MaxLimit</c>.
/// </summary>
public sealed record LibraryCoverSyncRequest(int? Limit);

/// <summary>
/// Body of a store price sync pass. <see cref="Limit"/> is optional: the service applies its own default
/// and rejects a value outside 1..<c>LibraryStorePriceLimits.Max</c>.
/// </summary>
public sealed record LibraryStorePriceSyncRequest(int? Limit);

/// <summary>
/// Wire contract of a store price pass. <see cref="Pending"/> is what the pass still has to visit when the
/// response leaves, so the caller loops on it instead of guessing a batch size.
/// </summary>
public sealed record LibraryStorePriceSyncResponse(
    int Pending,
    int Unsupported,
    int Updated,
    int Failed,
    int Rejected,
    int Remaining)
{
    public static LibraryStorePriceSyncResponse From(LibraryStorePriceSyncResult result) => new(
        result.Pending,
        result.Unsupported,
        result.Updated,
        result.Failed,
        result.Rejected,
        result.Remaining);
}

/// <summary>
/// Body of a manual addition (docs/PLAN_CONSOLE.md §5). <see cref="GameId"/> attaches the row to an
/// existing canonical game; <see cref="Create"/> forces a new canonical row when the typed title matches
/// existing games. With neither, matching titles are answered with <c>candidates</c> and nothing is
/// written: identity is never asserted by title. The client never sends a cover URL (same rule as
/// <c>covers/sync</c>): art is written later by a server pass.
/// </summary>
public sealed record ManualLibraryAddRequest(
    string? Store,
    string? Title,
    long? GameId,
    bool Create,
    bool? IsInstalled,
    DateTime? AddedAt,
    long? IgdbId);

/// <summary>Existing catalog row matching the typed title, for the manual-add dialog.</summary>
public sealed record GameCandidateResponse(long GameId, string Title, int? ReleaseYear, string? ImageUrl, bool InLibrary)
{
    public static GameCandidateResponse From(ManualGameCandidate candidate) =>
        new(candidate.GameId, candidate.Title, candidate.ReleaseYear, candidate.ImageUrl, candidate.InLibrary);
}

/// <summary>
/// Result of a manual addition. <see cref="Outcome"/> is one of <c>created | attached | duplicate |
/// candidates</c>; on <c>candidates</c> nothing was written and the list holds the choices.
/// </summary>
public sealed record ManualLibraryAddResponse(
    string Outcome,
    int Created,
    int Attached,
    int DuplicateRow,
    long GameId,
    long UserLibraryId,
    IReadOnlyList<GameCandidateResponse> Candidates)
{
    public static ManualLibraryAddResponse From(ManualLibraryAddResult result) => new(
        result.Outcome,
        result.Created,
        result.Attached,
        result.DuplicateRow,
        result.GameId,
        result.UserLibraryId,
        result.Candidates.Select(GameCandidateResponse.From).ToList());
}

/// <summary>One provider row from the manual-add title search (display only).</summary>
public sealed record ManualSearchHitResponse(
    long IgdbId,
    string Title,
    int? ReleaseYear,
    string? ImageUrl,
    IReadOnlyList<ManualSearchPlatformResponse> Platforms);

/// <summary>Console a search hit belongs to, as the provider reports it.</summary>
public sealed record ManualSearchPlatformResponse(int Id, string Name);

/// <summary>
/// Result of the manual-add title search. <see cref="Source"/> is <c>"igdb"</c> when the provider
/// answered and null when it is unconfigured or failed: the dialog must read that as "unavailable",
/// not as "no results". Read-only, never writes.
/// </summary>
public sealed record ManualSearchResponse(string? Source, IReadOnlyList<ManualSearchHitResponse> Hits)
{
    public static ManualSearchResponse From(ManualSearchResult result) => new(
        result.Source,
        result.Hits
            .Select(hit => new ManualSearchHitResponse(
                hit.SourceId,
                hit.Title,
                hit.ReleaseYear,
                hit.ImageUrl,
                hit.Platforms
                    .Select(platform => new ManualSearchPlatformResponse(platform.Id, platform.Name))
                    .ToList()))
            .ToList());
}

/// <summary>
/// Wire contract of a cover sync pass, mirroring <c>Deals.Web/lib/contracts/library-covers.ts</c>.
/// </summary>
public sealed record LibraryCoverSyncResponse(
    int Missing,
    int MissingWithoutSteamId,
    int Updated,
    int Failed,
    int Remaining)
{
    public static LibraryCoverSyncResponse From(LibraryCoverSyncResult result) => new(
        result.Missing,
        result.MissingWithoutSteamId,
        result.Updated,
        result.Failed,
        result.Remaining);
}
