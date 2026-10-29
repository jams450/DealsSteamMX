using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Services.Notifications;

/// <summary>
/// The "did the operator already buy this bundle?" gate. Both phases ask it: the producer before a row is
/// queued and the dispatcher again immediately before the HTTP call, so a purchase committed while a reminder
/// was already in the outbox is still observed.
/// </summary>
public interface IBundlePurchaseGate
{
    /// <summary>
    /// True when <paramref name="userId"/> has acquired the bundle identified by
    /// (<paramref name="source"/>, <paramref name="bundleKey"/>). Always scoped to one account: another
    /// person's purchase is never a suppression for this one.
    /// </summary>
    Task<bool> IsAcquiredAsync(int userId, string source, string bundleKey, CancellationToken cancellationToken);
}

/// <summary>
/// EF implementation over <c>bundle_purchases</c> (owned by the acquisition slice). It reuses
/// <see cref="BundlePurchaseRules.HidesBundle"/> instead of re-deriving the source family, so "the purchase
/// hides the card" and "the purchase silences the reminder" are one decision and cannot drift apart: the two
/// ITAD sources are the same family, gg.deals is its own.
///
/// <para>
/// The query is bounded twice: <c>user_id</c> and <c>bundle_key</c> both filter in SQL (the exact key match is a
/// precondition of the rule), and the family is applied in memory. A missing or invalid account answers false
/// without a query — the caller already failed closed on the operator gate, so this is defence in depth, not the
/// gate.
/// </para>
/// </summary>
public sealed class EfBundlePurchaseGate(IRepository repository) : IBundlePurchaseGate
{
    public async Task<bool> IsAcquiredAsync(
        int userId,
        string source,
        string bundleKey,
        CancellationToken cancellationToken)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(bundleKey))
        {
            return false;
        }

        var rows = await repository.Get<BundlePurchase>()
            .Where(purchase => purchase.UserId == userId && purchase.BundleKey == bundleKey)
            .Select(purchase => new { purchase.Source, purchase.BundleKey })
            .ToListAsync(cancellationToken);

        return rows.Any(row => BundlePurchaseRules.HidesBundle(row.Source, row.BundleKey, source, bundleKey));
    }
}
