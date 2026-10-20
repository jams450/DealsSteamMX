using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.GgDeals;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Atomic replacement of the gg.deals bundle snapshot. It writes under its own <see cref="Source"/>
/// literal so it is isolated from every other provider's purge: the per-game ITAD orphan purge is scoped
/// to <c>itad</c>, the global sweep to <c>itad_global</c>, and this writer touches only <c>ggdeals</c>.
/// The whole replace runs inside one transaction, so a crash mid-write leaves the previous snapshot
/// intact instead of a half-updated catalog. No HTTP happens here and nothing is ever written to
/// <c>game_offers</c>: a bundle is display data, not an offer.
/// </summary>
public sealed class EfGgDealsBundleSnapshotWriter(IRepository repository) : IGgDealsBundleSnapshotWriter
{
    /// <summary>Dedicated source and the literal the delete is scoped to.</summary>
    public const string Source = BundleCatalogOrder.GgDealsSource;

    private static readonly JsonSerializerOptions TierJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Upserts every bundle and deletes this source's rows no longer reported, inside one transaction.
    /// <c>ON CONFLICT</c> keeps the write atomic under concurrent runs and the delete is scoped to
    /// <see cref="Source"/> only, so offers, ITAD bundles and other sources are never touched.
    /// </summary>
    public async Task<GgDealsBundleWriteResult> ReplaceAsync(
        IReadOnlyList<GgDealsBundle> bundles,
        DateTime observedAt,
        CancellationToken cancellationToken)
    {
        var keys = bundles.Select(bundle => bundle.BundleKey).ToHashSet(StringComparer.Ordinal);

        return await repository.ExecuteInTransactionAsync(async () =>
        {
            foreach (var raw in bundles)
            {
                await repository.ExecuteSqlRawAsync(UpsertSql, BuildUpsertBindings(raw, observedAt));
            }

            var obsolete = await repository.GetTrack<ExternalBundle>()
                .Where(existing => existing.Source == Source && !keys.Contains(existing.BundleKey))
                .ToListAsync(cancellationToken);

            if (obsolete.Count == 0)
            {
                return new GgDealsBundleWriteResult(bundles.Count, 0);
            }

            await repository.RemoveRangeAsync(obsolete);
            return new GgDealsBundleWriteResult(bundles.Count, obsolete.Count);
        });
    }

    /// <summary>
    /// The statement of <see cref="ReplaceAsync"/>. Public, together with <see cref="BuildUpsertBindings"/>,
    /// so <c>checks/SqlParameters.Checks</c> can build this exact command through EF Core without a
    /// database and observe how a null provider field is bound.
    /// </summary>
    public const string UpsertSql = """
        INSERT INTO external_bundles
            (source, bundle_key, title, shop_id, shop_name, page_url, deal_url, details,
             published_at, tiers_json, expires_at, observed_at, updated_at)
        VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}::jsonb, {10}, {11}, NOW())
        ON CONFLICT (source, bundle_key) DO UPDATE SET
            title = EXCLUDED.title,
            shop_id = EXCLUDED.shop_id,
            shop_name = EXCLUDED.shop_name,
            page_url = EXCLUDED.page_url,
            deal_url = EXCLUDED.deal_url,
            details = EXCLUDED.details,
            published_at = EXCLUDED.published_at,
            tiers_json = EXCLUDED.tiers_json,
            expires_at = EXCLUDED.expires_at,
            observed_at = EXCLUDED.observed_at,
            updated_at = NOW()
        """;

    /// <summary>
    /// One binding per <c>{n}</c> placeholder of <see cref="UpsertSql"/>, in order. The mapping runs here
    /// so the persisted shape and the bindings cannot drift apart, and every argument is an explicitly
    /// typed <see cref="SqlParameters"/> value: an article with no link or no end date is ordinary provider
    /// data and must not abort the sweep (spec S1).
    /// </summary>
    public static object[] BuildUpsertBindings(GgDealsBundle raw, DateTime observedAt)
    {
        var bundle = GgDealsBundleMapper.Map(raw, observedAt);
        return
        [
            SqlParameters.Text("source", Source),
            SqlParameters.Text("bundle_key", bundle.BundleKey),
            SqlParameters.Text("title", bundle.Title),
            SqlParameters.Text("shop_id", bundle.ShopId),
            SqlParameters.Text("shop_name", bundle.ShopName),
            SqlParameters.Text("page_url", SafeGgDealsUrl(bundle.PageUrl)),
            // GG.deals publishes no affiliate redirector, so deal_url stays null and the page
            // URL is the only stored link.
            SqlParameters.Text("deal_url", null),
            SqlParameters.Text("details", bundle.Details),
            SqlParameters.TimestampTz("published_at", bundle.PublishedAt),
            SqlParameters.Text("tiers_json", SerializeTiers(bundle.Tiers)),
            SqlParameters.TimestampTz("expires_at", bundle.ExpiresAt),
            SqlParameters.TimestampTz("observed_at", observedAt),
        ];
    }

    private static string? SerializeTiers(IReadOnlyList<SteamGameBundleTier> tiers) =>
        tiers.Count == 0 ? null : JsonSerializer.Serialize(tiers, TierJsonOptions);

    /// <summary>
    /// Only a GG.deals host survives; HTTPS only, value verbatim. The article URL is nullable by
    /// contract, so null is a valid stored value rather than a reason to fail the bundle.
    /// </summary>
    private static string? SafeGgDealsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = uri.Host;
        var allowed = string.Equals(host, "gg.deals", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".gg.deals", StringComparison.OrdinalIgnoreCase);

        return allowed ? value : null;
    }
}
