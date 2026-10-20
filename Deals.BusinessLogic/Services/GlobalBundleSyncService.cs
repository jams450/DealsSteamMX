using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Itad;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Persists one completed global bundle snapshot. Kept as a narrow seam so the sweep service can be
/// exercised without a database: the production implementation is
/// <see cref="EfGlobalBundleSnapshotWriter"/>.
/// </summary>
public interface IGlobalBundleSnapshotWriter
{
    Task<GlobalBundleWriteResult> ReplaceAsync(
        IReadOnlyList<ItadBundle> bundles,
        DateTime observedAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Atomic replacement of the global ITAD bundle snapshot. It writes under its own
/// <see cref="Source"/> so the per-game ITAD orphan purge (which is scoped to <c>itad</c>) can never
/// delete a global row, and it removes only rows of that same source. The whole replace runs inside one
/// transaction: a crash mid-write leaves the previous snapshot intact rather than a half-updated catalog.
/// No HTTP happens here; the sweep has already finished in memory.
/// </summary>
public sealed class EfGlobalBundleSnapshotWriter(IRepository repository) : IGlobalBundleSnapshotWriter
{
    /// <summary>
    /// Dedicated source. Deliberately not <c>itad</c>: the per-game refresh purges orphaned
    /// <c>source = itad</c> bundles, so reusing that literal would delete the global snapshot.
    /// </summary>
    public const string Source = "itad_global";

    // Persistence caps mirror the per-game bundle writer so a global row is no larger than an
    // overview row. A truncation is marked incomplete by BundleTierMapper and never earns a share.
    private const int MaxTiersPerBundle = 20;
    private const int MaxTierGames = 50;

    private static readonly JsonSerializerOptions TierJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Upserts every bundle and deletes the source's rows no longer reported, inside one transaction.
    /// <c>ON CONFLICT</c> keeps the write atomic under concurrent runs and the delete is scoped to
    /// <see cref="Source"/> only, so offers, per-game bundles and other sources are never touched.
    /// </summary>
    public async Task<GlobalBundleWriteResult> ReplaceAsync(
        IReadOnlyList<ItadBundle> bundles,
        DateTime observedAt,
        CancellationToken cancellationToken)
    {
        var keys = bundles.Select(bundle => bundle.BundleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await repository.ExecuteInTransactionAsync(async () =>
        {
            foreach (var bundle in bundles)
            {
                await repository.ExecuteSqlRawAsync(UpsertSql, BuildUpsertBindings(bundle, observedAt));
            }

            var obsolete = await repository.GetTrack<ExternalBundle>()
                .Where(existing => existing.Source == Source && !keys.Contains(existing.BundleKey))
                .ToListAsync(cancellationToken);

            if (obsolete.Count == 0)
            {
                return new GlobalBundleWriteResult(bundles.Count, 0);
            }

            await repository.RemoveRangeAsync(obsolete);
            return new GlobalBundleWriteResult(bundles.Count, obsolete.Count);
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
    /// One binding per <c>{n}</c> placeholder of <see cref="UpsertSql"/>, in order. Every argument is an
    /// explicitly typed <see cref="SqlParameters"/> value: a bundle with no shop, no link and no dates is
    /// ordinary provider data, and it must not abort the sweep (spec S1).
    /// </summary>
    public static object[] BuildUpsertBindings(ItadBundle bundle, DateTime observedAt) =>
    [
        SqlParameters.Text("source", Source),
        SqlParameters.Text("bundle_key", bundle.BundleKey),
        SqlParameters.Text("title", bundle.Title),
        SqlParameters.Text("shop_id", bundle.ShopId),
        SqlParameters.Text("shop_name", bundle.ShopName),
        SqlParameters.Text("page_url", SafeItadUrl(bundle.PageUrl)),
        SqlParameters.Text("deal_url", SafeItadUrl(bundle.Url)),
        SqlParameters.Text("details", bundle.Details),
        SqlParameters.TimestampTz("published_at", bundle.PublishedAt),
        SqlParameters.Text("tiers_json", SerializeTiers(bundle.Tiers)),
        SqlParameters.TimestampTz("expires_at", bundle.ExpiresAt),
        SqlParameters.TimestampTz("observed_at", observedAt),
    ];

    private static string? SerializeTiers(IReadOnlyList<ItadBundleTier> tiers)
    {
        var mapped = BundleTierMapper.Map(tiers, MaxTiersPerBundle, MaxTierGames);
        return mapped.Count == 0 ? null : JsonSerializer.Serialize(mapped, TierJsonOptions);
    }

    /// <summary>
    /// Same host policy the per-game writer applies to ITAD links: only the store page or the ITAD
    /// affiliate redirector survives, HTTPS only, value verbatim. The affiliate tag is never rewritten.
    /// </summary>
    private static string? SafeItadUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = uri.Host;
        var allowed = string.Equals(host, "isthereanydeal.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".isthereanydeal.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "itad.link", StringComparison.OrdinalIgnoreCase);

        return allowed ? value : null;
    }
}

/// <summary>
/// Bounded, paginated ingestion of the global ITAD bundle catalog. It fetches pages through
/// <see cref="IItadClient.GetGlobalBundlesPageAsync"/> and hands the ordered pages to
/// <see cref="GlobalBundleSweep"/>; only a complete sweep reaches the snapshot writer. Any failure,
/// truncation or repeated page returns a preserved result and writes nothing, so the previously observed
/// snapshot and its timestamp stand. Provider calls happen before the transaction and are never made
/// inside it.
/// </summary>
public sealed class GlobalBundleSyncService(
    IItadClient itadClient,
    IGlobalBundleSnapshotWriter snapshotWriter,
    GlobalBundleSyncSettings settings,
    ILogger<GlobalBundleSyncService> logger)
{
    /// <summary>Job name recorded in <c>job_runs</c>.</summary>
    public const string JobName = "bundles.global-sync";

    /// <summary>
    /// PostgreSQL session advisory lock key of the sweep. Distinct from the other session locks in use:
    /// the wishlist sync lock <c>7_831_442_091</c> and the ntfy deals lock <c>7_831_442_092</c>. Sharing a
    /// key would make one background job silently skip the other, and the per-user category locks use the
    /// disjoint <c>0x574C00000000L</c> range. Kept public so the checks can assert the separation.
    /// </summary>
    public const long AdvisoryLockKey = 7_831_442_093L;

    public async Task<GlobalBundleSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var sweep = new GlobalBundleSweep(settings.PageSize, settings.MaxPages);
        var offset = 0;

        while (sweep.NeedsAnotherPage)
        {
            IReadOnlyList<ItadBundle> page;
            try
            {
                page = await itadClient.GetGlobalBundlesPageAsync(offset, settings.PageSize, cancellationToken);
            }
            catch (Exception exception) when (IsPageFailure(exception, cancellationToken))
            {
                sweep.MarkFailed();
                break;
            }

            if (!sweep.AddPage(page))
            {
                break;
            }

            offset += settings.PageSize;
        }

        if (!sweep.IsComplete)
        {
            logger.LogWarning(
                "[bundles.global] sweep incomplete reason={Reason} pages={Pages} seen={Seen}; snapshot preserved",
                sweep.IncompleteReason,
                sweep.PageCount,
                sweep.Bundles.Count);
            return GlobalBundleSyncResult.Preserved(sweep);
        }

        var write = await snapshotWriter.ReplaceAsync(sweep.Bundles, DateTime.UtcNow, cancellationToken);
        logger.LogInformation(
            "[bundles.global] sweep complete pages={Pages} bundles={Bundles} written={Written} removed={Removed}",
            sweep.PageCount,
            sweep.Bundles.Count,
            write.Written,
            write.Removed);
        return GlobalBundleSyncResult.Applied(sweep, write);
    }

    /// <summary>
    /// A provider failure, a malformed payload or a provider-caused timeout degrades to "preserve"; a
    /// caller cancellation (shutdown) is not degraded and must propagate so the host stops quickly.
    /// </summary>
    private static bool IsPageFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or JsonException or OperationCanceledException;
}
