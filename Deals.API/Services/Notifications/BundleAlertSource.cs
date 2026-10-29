using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Services.Notifications;

/// <summary>Current persisted state of one bundle, re-read before every send.</summary>
public sealed record BundleAlertCurrent(string Source, string BundleKey, DateTime? ExpiresAt);

/// <summary>
/// Read-only view of the bundle catalog the reminder needs. Two operations: the pending set the producer
/// evaluates, and the current state of one bundle the dispatcher revalidates a queued row against. It never
/// writes, never calls a provider and never touches <c>game_offers</c>: a bundle is not a price.
/// </summary>
public interface IBundleAlertSource
{
    /// <summary>Active bundles whose persisted expiry is inside the red window at <paramref name="nowUtc"/>.</summary>
    Task<IReadOnlyList<BundleAlertCandidate>> LoadPendingAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// The current snapshot of the bundle a queued row was built from, or null when it no longer exists in the
    /// catalog. The row's own snapshot is never trusted for this: it is the expiry the reminder was queued for,
    /// not the expiry the provider has now.
    /// </summary>
    Task<BundleAlertCurrent?> FindCurrentAsync(string source, string bundleKey, CancellationToken cancellationToken);
}

/// <summary>
/// EF implementation over <c>external_bundles</c>. The SQL prefilter is the bounded window (only rows that
/// expire within <see cref="BundleAlertPolicy.AlertWindowHours"/> are loaded, so the reminder never scans the
/// whole catalog) and the dedup mirrors the catalog page exactly:
/// <see cref="BundleCatalogOrder.Deduplicate"/> collapses the two ITAD sources into one card per bundle key and
/// never merges by title. A reminder therefore always describes the same card the person sees.
/// </summary>
public sealed class EfBundleAlertSource(IRepository repository) : IBundleAlertSource
{
    /// <summary>Every source the bundle catalog reads. gg.deals keeps its own key space.</summary>
    private static readonly string[] CatalogSources =
        [BundleCatalogOrder.ItadSource, BundleCatalogOrder.ItadGlobalSource, BundleCatalogOrder.GgDealsSource];

    public async Task<IReadOnlyList<BundleAlertCandidate>> LoadPendingAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var windowEnd = nowUtc.AddHours(BundleAlertPolicy.AlertWindowHours);

        var rows = await repository.Get<ExternalBundle>()
            .Where(bundle => CatalogSources.Contains(bundle.Source))
            .Where(bundle => bundle.ExpiresAt != null && bundle.ExpiresAt > nowUtc && bundle.ExpiresAt <= windowEnd)
            .Select(bundle => new
            {
                bundle.Source,
                bundle.BundleKey,
                bundle.Title,
                bundle.ShopName,
                bundle.PageUrl,
                bundle.DealUrl,
                bundle.ExpiresAt,
                bundle.ObservedAt
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        // The same dedup the catalog applies: family + bundle key, newest observation wins. A bundle reported by
        // both ITAD sources is one reminder; two bundles that share a title are two.
        var winners = BundleCatalogOrder.Deduplicate(rows.Select(row => new BundleCatalogSnapshot(
            row.Source,
            row.BundleKey,
            row.Title,
            row.ExpiresAt,
            row.ObservedAt)));

        var byIdentity = rows
            .GroupBy(row => (row.Source, row.BundleKey))
            .ToDictionary(group => group.Key, group => group.First());

        var candidates = new List<BundleAlertCandidate>(winners.Count);
        foreach (var winner in winners)
        {
            if (!byIdentity.TryGetValue((winner.Source, winner.BundleKey), out var row) ||
                row.ExpiresAt is not { } expiresAt)
            {
                continue;
            }

            candidates.Add(new BundleAlertCandidate(
                row.Source,
                row.BundleKey,
                row.Title,
                row.ShopName,
                row.PageUrl,
                row.DealUrl,
                expiresAt,
                row.ObservedAt));
        }

        return candidates;
    }

    public async Task<BundleAlertCurrent?> FindCurrentAsync(
        string source,
        string bundleKey,
        CancellationToken cancellationToken)
    {
        var rows = await repository.Get<ExternalBundle>()
            .Where(bundle => CatalogSources.Contains(bundle.Source) && bundle.BundleKey == bundleKey)
            .Select(bundle => new
            {
                bundle.Source,
                bundle.BundleKey,
                bundle.Title,
                bundle.ExpiresAt,
                bundle.ObservedAt
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return null;
        }

        // Same winner the producer would pick today, so a bundle the provider re-published under the other ITAD
        // source is revalidated against the snapshot the catalog shows instead of a stale sibling row.
        var family = BundleAlertPolicy.SourceFamily(source);
        var winner = BundleCatalogOrder.Deduplicate(rows
                .Where(row => string.Equals(BundleAlertPolicy.SourceFamily(row.Source), family, StringComparison.Ordinal))
                .Select(row => new BundleCatalogSnapshot(row.Source, row.BundleKey, row.Title, row.ExpiresAt, row.ObservedAt)))
            .FirstOrDefault();

        return winner is null ? null : new BundleAlertCurrent(winner.Source, winner.BundleKey, winner.ExpiresAt);
    }
}
