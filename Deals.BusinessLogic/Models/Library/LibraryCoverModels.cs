namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Bounds of a cover sync pass. They live with the wire model, not inside the service, so the API validates
/// the request against the same numbers the service honours.
/// </summary>
public static class LibraryCoverLimits
{
    /// <summary>Default pass size: a click the user can wait on without a progress bar.</summary>
    public const int Default = 25;

    /// <summary>
    /// Games a single pass will visit. A visited game costs between one and six provider requests — the
    /// cheapest case is a game whose Steam appid the catalog already knows and whose header image comes back
    /// first try; the worst is a game no provider recognises, which walks Steam by appid, Steam by title
    /// (search plus details), IGDB and SteamGridDB (autocomplete plus grids) before giving up. Multiplied by
    /// this cap that is the ceiling of a bounded pass, and the reason the pass stays sequential.
    /// </summary>
    public const int Max = 100;
}

/// <summary>
/// Outcome of one cover sync pass. Counts, never a list of titles: the page says how many covers moved, from
/// which provider, and what happened to the rest.
/// <see cref="Missing"/> counts distinct canonical games of the caller's library that have no cover at all;
/// <see cref="Updated"/> is how many of those the pass filled, split by <see cref="UpdatedBySteam"/> (either
/// Steam step: a known appid or a title search), <see cref="UpdatedByIgdb"/> and
/// <see cref="UpdatedBySteamGridDb"/>.
/// <see cref="Unmatched"/> is the games whose whole chain answered without a usable URL — nothing is broken,
/// no source had art for that title — and <see cref="Failed"/> is the games where at least one source could
/// not be consulted at all (transport error, unconfigured, malformed payload). Both stay for the next pass
/// and neither aborts it. <see cref="Remaining"/> are the games the pass did not visit because it hit its
/// limit: the pass is capped on purpose, so the button advances one batch at a time.
/// </summary>
public sealed record LibraryCoverSyncResult(
    int Missing,
    int Updated,
    int UpdatedBySteam,
    int UpdatedByIgdb,
    int UpdatedBySteamGridDb,
    int Unmatched,
    int Failed,
    int Remaining);

/// <summary>
/// Where a manual cover pick comes from. The source is what the server re-reads: the client only picks an
/// id, and the stored URL is always the one the provider returned.
/// </summary>
public enum GameCoverSource
{
    /// <summary>Steam CDN header image of an appid.</summary>
    Steam,

    /// <summary>IGDB cover of an IGDB game id.</summary>
    Igdb,

    /// <summary>SteamGridDB grid of a SteamGridDB game id.</summary>
    SteamGridDb
}

/// <summary>
/// One manual cover pick, already discriminated: exactly one of the three ids is set and the other two are
/// null. No URL and no title ever travels in this command. The API maps its request shape to it and the service
/// re-validates, so an ambiguous body is a 400 before any provider call or write.
/// </summary>
public sealed record GameCoverCommand(
    GameCoverSource Source,
    int? SteamAppId,
    long? IgdbId,
    int? SteamGridDbId);
