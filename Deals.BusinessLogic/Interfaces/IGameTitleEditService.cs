using Deals.BusinessLogic.Models.Catalog;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Edit of the canonical title of a game (<c>games.title</c> plus <c>games.normalized_title</c>). This is
/// the only writer of that pair post-creation: a Playnite reimport keeps writing <c>user_library.title</c>,
/// and the library read-model projects the canonical title when a row has an identity, so the grid shows
/// what the catalog says and the reimport cannot overwrite it.
///
/// Only two things are ever written: the title pair, and — in IGDB mode — the exact <c>('igdb', id)</c>
/// mapping. No merge, no library rows, no reviews, no covers and no release year.
/// </summary>
public interface IGameTitleEditService
{
    /// <summary>
    /// Applies one title edit in a single transaction under the global identity lock. Returns
    /// <see cref="GameTitleEditResult.Conflict"/> with a safe reason instead of throwing when an
    /// identifier belongs to another game or the target is already linked to a different IGDB row.
    /// Throws <c>GameNotFoundException</c> when the canonical game does not exist, and the
    /// <c>GameTitleSource*</c> exceptions when an IGDB lookup is unavailable or empty.
    /// </summary>
    Task<GameTitleEditResult> EditTitleAsync(
        long gameId,
        GameTitleEditCommand command,
        CancellationToken cancellationToken = default);
}
