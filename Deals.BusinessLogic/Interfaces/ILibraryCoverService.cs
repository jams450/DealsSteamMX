using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Cover art of the canonical catalog, taken from Steam and stored as a URL only (never the image bytes),
/// the same rule the wishlist follows. Writing a cover is display-only: no identity is claimed, no price is
/// touched and no library row changes. A cover already present is never overwritten by a sync; only an
/// explicit pick replaces it.
/// </summary>
public interface ILibraryCoverService
{
    /// <summary>
    /// Fills the missing covers of the caller's library from the Steam appids the catalog already knows.
    /// Reads one Steam request per game and stops at <paramref name="limit"/> games, so a click is bounded:
    /// whatever is left comes back as <see cref="LibraryCoverSyncResult.Remaining"/>.
    /// </summary>
    Task<LibraryCoverSyncResult> SyncMissingCoversAsync(
        int userId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Places the cover of one canonical game from an appid the caller picked, replacing any previous cover.
    /// The URL comes from Steam, never from the request: the client sends the appid, not the URL.
    /// </summary>
    Task<string> SetCoverFromSteamAsync(long gameId, int steamAppId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Places the cover of one canonical game from an IGDB id the caller picked, replacing any previous
    /// cover. The server re-reads the row before opening any transaction and stores only the URL IGDB
    /// returned on <c>games.image_url</c>: no identity mapping, no title, no release year, no library row,
    /// no review and no price. Throws <c>GameNotFoundException</c> when the canonical game does not exist,
    /// <c>GameCoverSourceNotFoundException</c> when IGDB answers without that row or without a cover, and
    /// <c>GameCoverSourceUnavailableException</c> when IGDB cannot be consulted at all. Every failure writes
    /// nothing.
    /// </summary>
    Task<string> SetCoverFromIgdbAsync(long gameId, long igdbId, CancellationToken cancellationToken = default);
}
