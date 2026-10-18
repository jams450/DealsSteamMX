using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Persists one completed global deals snapshot. Kept as a narrow seam so the sweep service can be
/// exercised without a database: the production implementation is <see cref="EfGlobalDealSnapshotWriter"/>.
/// </summary>
public interface IGlobalDealSnapshotWriter
{
    Task<GlobalDealWriteResult> ReplaceAsync(
        IReadOnlyList<ItadGlobalDeal> deals,
        DateTime observedAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Atomic replacement of the global ITAD deals snapshot. It writes under its own
/// <see cref="GlobalDealSources.GlobalDeals"/> source so no other provider cleanup can delete it, and
/// it removes only rows of that same source. The whole replace runs inside one transaction: a crash
/// mid-write leaves the previous snapshot intact rather than a half-updated feed.
///
/// The MXN projection is computed before the transaction from the latest persisted FX rate
/// (<see cref="IFxRateService.GetLatestRateAsync"/>, read-only, never a provider fetch). No HTTP
/// happens here; the sweep has already finished in memory.
/// </summary>
public sealed class EfGlobalDealSnapshotWriter(IRepository repository, IFxRateService fxRateService)
    : IGlobalDealSnapshotWriter
{
    public async Task<GlobalDealWriteResult> ReplaceAsync(
        IReadOnlyList<ItadGlobalDeal> deals,
        DateTime observedAt,
        CancellationToken cancellationToken)
    {
        var keys = deals.Select(deal => deal.DealKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // One read for the whole page, and only when a USD deal needs it. Read-only by contract.
        GlobalDealFxRate? usdToMxn = null;
        if (deals.Any(deal => string.Equals(deal.Currency, GlobalDealCurrency.Usd, StringComparison.Ordinal)))
        {
            var rate = await fxRateService.GetLatestRateAsync(
                GlobalDealCurrency.Usd,
                GlobalDealCurrency.Mxn,
                cancellationToken);
            if (rate is { Rate: > 0 })
            {
                usdToMxn = new GlobalDealFxRate(rate.Rate, rate.RateDate, rate.Source);
            }
        }

        return await repository.ExecuteInTransactionAsync(async () =>
        {
            foreach (var deal in deals)
            {
                var projection = GlobalDealCurrency.Project(deal.Currency, deal.CurrentPriceMinor, usdToMxn);
                await repository.ExecuteSqlRawAsync(
                    UpsertSql,
                    GlobalDealSources.GlobalDeals,
                    deal.DealKey,
                    deal.ItadGameId,
                    deal.GameTitle,
                    (object?)deal.GameSlug ?? DBNull.Value,
                    (object?)deal.GameType ?? DBNull.Value,
                    deal.GameMature,
                    deal.ShopId,
                    deal.ShopName,
                    deal.IsOfficial,
                    deal.Currency,
                    deal.CurrentPriceMinor,
                    (object?)deal.RegularPriceMinor ?? DBNull.Value,
                    (object?)deal.DiscountPercent ?? DBNull.Value,
                    (object?)deal.HistoryLowMinor ?? DBNull.Value,
                    (object?)deal.HistoryLowCurrency ?? DBNull.Value,
                    (object?)projection.MxnCurrentPriceMinor ?? DBNull.Value,
                    (object?)projection.FxRate ?? DBNull.Value,
                    (object?)projection.FxRateDate ?? DBNull.Value,
                    (object?)projection.FxSource ?? DBNull.Value,
                    projection.PricingType,
                    (object?)deal.DealUrl ?? DBNull.Value,
                    (object?)deal.ExpiresAt ?? DBNull.Value,
                    observedAt);
            }

            var obsolete = await repository.GetTrack<GlobalDeal>()
                .Where(existing => existing.Source == GlobalDealSources.GlobalDeals &&
                    !keys.Contains(existing.DealKey))
                .ToListAsync(cancellationToken);

            if (obsolete.Count == 0)
            {
                return new GlobalDealWriteResult(deals.Count, 0);
            }

            await repository.RemoveRangeAsync(obsolete);
            return new GlobalDealWriteResult(deals.Count, obsolete.Count);
        });
    }

    private const string UpsertSql = """
        INSERT INTO global_deals
            (source, deal_key, itad_game_id, game_title, game_slug, game_type, game_mature,
             shop_id, shop_name, is_official, original_currency, original_current_price_minor,
             original_regular_price_minor, discount_percent, history_low_all_minor, history_low_currency,
             mxn_current_price_minor, fx_rate, fx_rate_date, fx_source, pricing_type, deal_url, expires_at,
             observed_at, created_at, updated_at)
        VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11},
                {12}, {13}, {14}, {15}, {16}, {17}, {18}, {19}, {20}, {21}, {22}, {23}, NOW(), NOW())
        ON CONFLICT (source, deal_key) DO UPDATE SET
            itad_game_id = EXCLUDED.itad_game_id,
            game_title = EXCLUDED.game_title,
            game_slug = EXCLUDED.game_slug,
            game_type = EXCLUDED.game_type,
            game_mature = EXCLUDED.game_mature,
            shop_id = EXCLUDED.shop_id,
            shop_name = EXCLUDED.shop_name,
            is_official = EXCLUDED.is_official,
            original_currency = EXCLUDED.original_currency,
            original_current_price_minor = EXCLUDED.original_current_price_minor,
            original_regular_price_minor = EXCLUDED.original_regular_price_minor,
            discount_percent = EXCLUDED.discount_percent,
            history_low_all_minor = EXCLUDED.history_low_all_minor,
            history_low_currency = EXCLUDED.history_low_currency,
            mxn_current_price_minor = EXCLUDED.mxn_current_price_minor,
            fx_rate = EXCLUDED.fx_rate,
            fx_rate_date = EXCLUDED.fx_rate_date,
            fx_source = EXCLUDED.fx_source,
            pricing_type = EXCLUDED.pricing_type,
            deal_url = EXCLUDED.deal_url,
            expires_at = EXCLUDED.expires_at,
            observed_at = EXCLUDED.observed_at,
            updated_at = NOW()
        """;
}

/// <summary>
/// Bounded, paginated ingestion of the global ITAD deals catalog. It fetches pages through
/// <see cref="IGlobalDealClient.GetDealsPageAsync"/> and hands them to <see cref="GlobalDealSweep"/>;
/// only a complete sweep reaches the snapshot writer. Any failure, truncation or cursor loop returns a
/// preserved result and writes nothing, so the previously observed snapshot and its timestamp stand.
/// Provider calls happen before the transaction and are never made inside it.
///
/// Only deals the configured official allowlist recognizes are persisted: the provider is queried with
/// that allowlist but the code does not trust the provider to honor it.
/// </summary>
public sealed class GlobalDealSyncService(
    IGlobalDealClient globalDealClient,
    IGlobalDealSnapshotWriter snapshotWriter,
    GlobalDealSyncSettings settings,
    ILogger<GlobalDealSyncService> logger)
{
    /// <summary>Job name recorded in <c>job_runs</c>.</summary>
    public const string JobName = "deals.global-sync";

    /// <summary>
    /// PostgreSQL session advisory lock key of the sweep. Distinct from the wishlist sync
    /// <c>7_831_442_091</c>, the ntfy deals <c>7_831_442_092</c> and the global bundle sweep
    /// <c>7_831_442_093</c>. Sharing a key would make one background job silently skip the other.
    /// Kept public so the checks can assert the separation.
    /// </summary>
    public const long AdvisoryLockKey = 7_831_442_095L;

    public async Task<GlobalDealSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var sweep = new GlobalDealSweep(settings.PageSize, settings.MaxPages);
        var offset = 0;

        while (sweep.NeedsAnotherPage)
        {
            GlobalDealPage page;
            try
            {
                page = await globalDealClient.GetDealsPageAsync(offset, settings.PageSize, cancellationToken);
            }
            catch (Exception exception) when (IsPageFailure(exception, cancellationToken))
            {
                sweep.MarkFailed();
                break;
            }

            if (!sweep.AddPage(page, offset))
            {
                break;
            }

            offset = page.NextOffset
                ?? throw new InvalidOperationException("A continuing deals page must carry a cursor.");
        }

        if (!sweep.IsComplete)
        {
            logger.LogWarning(
                "[deals.global] sweep incomplete reason={Reason} pages={Pages} seen={Seen}; snapshot preserved",
                sweep.IncompleteReason,
                sweep.PageCount,
                sweep.Deals.Count);
            return GlobalDealSyncResult.Preserved(sweep);
        }

        var official = sweep.Deals.Where(deal => deal.IsOfficial).ToList();
        var skipped = sweep.Deals.Count - official.Count;
        if (skipped > 0)
        {
            logger.LogInformation(
                "[deals.global] dropped {Skipped} deals whose shop is outside the official allowlist",
                skipped);
        }

        var write = await snapshotWriter.ReplaceAsync(official, DateTime.UtcNow, cancellationToken);
        logger.LogInformation(
            "[deals.global] sweep complete pages={Pages} deals={Deals} written={Written} removed={Removed}",
            sweep.PageCount,
            official.Count,
            write.Written,
            write.Removed);
        return GlobalDealSyncResult.Applied(sweep, write);
    }

    /// <summary>
    /// A provider failure, a malformed payload or a provider-caused timeout degrades to "preserve"; a
    /// caller cancellation (shutdown) is not degraded and must propagate so the host stops quickly.
    /// </summary>
    private static bool IsPageFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException;
}
