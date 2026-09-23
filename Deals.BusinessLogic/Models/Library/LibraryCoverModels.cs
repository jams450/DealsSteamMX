namespace Deals.BusinessLogic.Models.Library;

/// <summary>
/// Bounds of a cover sync pass. They live with the wire model, not inside the service, so the API validates
/// the request against the same numbers the service honours.
/// </summary>
public static class LibraryCoverLimits
{
    /// <summary>Default pass size: a click the user can wait on without a progress bar.</summary>
    public const int Default = 25;

    /// <summary>Games a single pass will ask Steam about. Bigger batches are a follow-up, not a first step.</summary>
    public const int Max = 100;
}

/// <summary>
/// Outcome of one cover sync pass. Counts, never a list of titles: the page says how many covers moved and
/// how many rows still need a manual pick. <see cref="Missing"/> counts distinct canonical games of the
/// caller's library that have no cover at all; <see cref="MissingWithoutSteamId"/> are those the pass
/// cannot solve by itself, because the catalog does not know a Steam appid for them.
/// <see cref="Remaining"/> are rows that do have an appid but were left for the next pass: one pass is
/// capped on purpose, since every row costs one Steam request.
/// </summary>
public sealed record LibraryCoverSyncResult(
    int Missing,
    int MissingWithoutSteamId,
    int Updated,
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
    Igdb
}

/// <summary>
/// One manual cover pick, already discriminated: exactly one of the two ids is set and the other is null.
/// No URL and no title ever travels in this command. The API maps its request shape to it and the service
/// re-validates, so an ambiguous body is a 400 before any provider call or write.
/// </summary>
public sealed record GameCoverCommand(GameCoverSource Source, int? SteamAppId, long? IgdbId);
