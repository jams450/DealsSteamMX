using Deals.BusinessLogic.Models.Bundles;

namespace Deals.BusinessLogic.Interfaces;

/// <summary>
/// Tier-aware bundle acquisition (specs S3-S6): the read-only preview the buyer confirms, the atomic
/// purchase that snapshots the tier, imports the owned games and clears their Steam wishlist rows, the
/// caller's purchase inventory and duplicate inventory, and the read-only canonical candidate search used
/// to link an item the provider did not identify.
///
/// <para>
/// Every member takes the user id from the caller's session; no member accepts a user id from the client,
/// so a caller can only ever read or write their own records.
/// </para>
/// </summary>
public interface IBundlePurchaseService
{
    /// <summary>Preview of one persisted bundle, or null when no such bundle snapshot exists.</summary>
    Task<BundlePurchasePreview?> PreviewAsync(
        int userId,
        string? source,
        string? bundleKey,
        string? platform,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms one purchase in a single transaction under the shared lock order (per-user wishlist gate,
    /// then the global identity gate). Returns null when the bundle snapshot does not exist; a repeated
    /// submit returns the stored purchase untouched (<c>Created = false</c>).
    /// </summary>
    Task<BundlePurchaseResult?> PurchaseAsync(
        int userId,
        BundlePurchaseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>The caller's purchases, newest first, with the games each one imported or found owned.</summary>
    Task<IReadOnlyList<BundlePurchaseRecord>> ListAsync(
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>The caller's duplicate inventory: games a purchase skipped because they were already owned.</summary>
    Task<IReadOnlyList<BundlePurchaseDuplicateEntry>> ListDuplicatesAsync(
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only search of existing canonical games by title, for manually linking an item the provider
    /// identified poorly. It never creates a game and never selects one: the person sends the chosen id back
    /// in the confirm.
    /// </summary>
    Task<IReadOnlyList<BundlePurchaseCandidate>> FindCandidatesAsync(
        int userId,
        string? query,
        int? limit,
        CancellationToken cancellationToken = default);
}
