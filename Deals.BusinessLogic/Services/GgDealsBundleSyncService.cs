using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.GgDeals;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Persists one completed gg.deals active snapshot. Kept as a narrow seam so the sweep service can be
/// exercised without a database: the production implementation is
/// <see cref="EfGgDealsBundleSnapshotWriter"/>.
/// </summary>
public interface IGgDealsBundleSnapshotWriter
{
    Task<GgDealsBundleWriteResult> ReplaceAsync(
        IReadOnlyList<GgDealsBundle> bundles,
        DateTime observedAt,
        CancellationToken cancellationToken);
}

/// <summary>Rows written and removed by one atomic gg.deals snapshot replacement.</summary>
public sealed record GgDealsBundleWriteResult(int Written, int Removed);

/// <summary>
/// Bounded, cursor-paginated ingestion of the gg.deals active bundle catalog. It fetches one page at a
/// time through <see cref="IGgDealsClient.GetActiveBundlesAsync"/> and hands each page to
/// <see cref="GgDealsBundleSweep"/>; only a complete sweep reaches the snapshot writer. Any failure,
/// truncation, repeated cursor/page, total mismatch or expired cursor returns a preserved result and
/// writes nothing, so the previously observed snapshot and its timestamp stand. Provider calls happen
/// before the transaction and are never made inside it.
/// </summary>
public sealed class GgDealsBundleSyncService(
    IGgDealsClient ggDealsClient,
    IGgDealsBundleSnapshotWriter snapshotWriter,
    GgDealsBundleSyncSettings settings,
    ILogger<GgDealsBundleSyncService> logger)
{
    /// <summary>Job name recorded in <c>job_runs</c>.</summary>
    public const string JobName = "bundles.ggdeals-sync";

    /// <summary>
    /// PostgreSQL session advisory lock key of the gg.deals sweep. Distinct from the wishlist sync
    /// <c>7_831_442_091</c>, the ntfy deals <c>7_831_442_092</c> and the global ITAD bundle sweep
    /// <c>7_831_442_093</c>. Sharing a key would make one background job silently skip the other. Kept
    /// public so the checks can assert the separation.
    /// </summary>
    public const long AdvisoryLockKey = 7_831_442_094L;

    public async Task<GgDealsBundleSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var sweep = new GgDealsBundleSweep(
            settings.MaxPages,
            TimeSpan.FromMinutes(Math.Max(1, settings.CursorValidityMinutes)));
        string? cursor = null;

        while (sweep.NeedsAnotherPage)
        {
            GgDealsBundlePage page;
            try
            {
                page = await ggDealsClient.GetActiveBundlesAsync(cursor, cancellationToken);
            }
            catch (Exception exception) when (IsPageFailure(exception, cancellationToken))
            {
                sweep.MarkFailed();
                break;
            }

            if (!sweep.AddPage(page, DateTime.UtcNow))
            {
                break;
            }

            cursor = page.NextCursor;
        }

        if (!sweep.IsComplete)
        {
            logger.LogWarning(
                "[bundles.ggdeals] sweep incomplete reason={Reason} pages={Pages} seen={Seen}; snapshot preserved",
                sweep.IncompleteReason,
                sweep.PageCount,
                sweep.Bundles.Count);
            return GgDealsBundleSyncResult.Preserved(sweep);
        }

        var write = await snapshotWriter.ReplaceAsync(sweep.Bundles, DateTime.UtcNow, cancellationToken);
        logger.LogInformation(
            "[bundles.ggdeals] sweep complete pages={Pages} bundles={Bundles} written={Written} removed={Removed}",
            sweep.PageCount,
            sweep.Bundles.Count,
            write.Written,
            write.Removed);
        return GgDealsBundleSyncResult.Applied(sweep, write.Written, write.Removed);
    }

    /// <summary>
    /// A provider failure, a malformed payload or a provider-caused timeout degrades to "preserve"; a
    /// caller cancellation (shutdown) is not degraded and must propagate so the host stops quickly.
    /// </summary>
    private static bool IsPageFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or JsonException or OperationCanceledException;
}
