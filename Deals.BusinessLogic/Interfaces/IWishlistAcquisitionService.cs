using Deals.BusinessLogic.Models.Library;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Wishlist → library acquisition (specs S1/S2/S3): register that a wished game was bought, gifted,
/// claimed for free, obtained in a bundle or accessed through a subscription, on a chosen platform, and
/// optionally drop it from the local wishlist so the next Steam sync does not put it back.
///
/// <para>
/// One call is one transaction: the per-user wishlist gate (shared with the Steam sync), the canonical
/// identity gate, the duplication check, the library row, the suppression row and the removal of the wished
/// row all commit together or not at all. Repeating the same submit is idempotent — <c>already_owned</c> with
/// the same row id — and never rewrites metadata an earlier submit (or an import) already stored. That holds
/// after a dismissal too: the repeated submit finds the wished row gone, and the stored suppression plus the
/// existing library row answer <c>already_owned</c> instead of a 404.
/// </para>
/// </summary>
public interface IWishlistAcquisitionService
{
    /// <summary>
    /// Acquires one wished Steam appid. Returns <c>null</c> when the appid is not in this user's wishlist, when
    /// it has no canonical identity yet, or when a repeated submit finds it dismissed but the chosen platform
    /// holds no owned/subscription row: the route answers 404 and nothing is written. Throws
    /// <see cref="ArgumentException"/> (400) for an invalid payload and
    /// <see cref="Exceptions.IdentityLockUnavailableException"/> (409) when another write holds the gate.
    /// </summary>
    Task<WishlistAcquisitionResult?> AcquireAsync(
        int userId,
        int appId,
        WishlistAcquisitionRequest request,
        CancellationToken cancellationToken = default);
}
