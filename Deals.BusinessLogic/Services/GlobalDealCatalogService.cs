using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Discovery;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Read-only catalog over the persisted global deals snapshot. It answers <c>GET /api/deals</c> from
/// rows already observed and written by the background sweep: no provider call happens here, no deal is
/// written, and no amount read here enters <c>game_offers</c> or any savings math.
///
/// The feed is the one spec S1 asked for: good deals for games the authenticated user has not
/// wishlisted yet. The user id arrives from the controller (never the query string) and is used only to
/// resolve that user's wished Steam rows. A non-wishlist good deal is always included; a wishlisted game
/// is intentionally left to the wishlist page.
/// </summary>
public sealed class GlobalDealCatalogService(IRepository repository, GlobalDealCatalogSettings settings)
{
    public async Task<GlobalDealCatalog> ListAsync(
        int userId,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var wishedItadIds = await ResolveWishlistItadIdsAsync(userId, cancellationToken);

        // Eligibility and quality are the persisted projection's own fields: the MXN figure exists
        // (regional or converted) and the documented discount clears the threshold, or the current
        // price is at a provider-reported low in the same currency. A foreign unconverted deal has no
        // MXN amount and is therefore absent from the threshold branch.
        var query = repository.Get<GlobalDeal>()
            .Where(deal => deal.Source == GlobalDealSources.GlobalDeals &&
                deal.IsOfficial &&
                deal.GameType == "game" &&
                (deal.PricingType == GlobalDealPricingTypes.Regional ||
                 deal.PricingType == GlobalDealPricingTypes.FxEstimate) &&
                (deal.ExpiresAt == null || deal.ExpiresAt > now) &&
                !wishedItadIds.Contains(deal.ItadGameId) &&
                ((deal.DiscountPercent != null && deal.DiscountPercent >= settings.MinDiscountPercent) ||
                 (deal.HistoryLowAllMinor != null && deal.HistoryLowAllMinor > 0 &&
                  deal.HistoryLowCurrency == deal.OriginalCurrency &&
                  deal.OriginalCurrentPriceMinor > 0 &&
                  deal.OriginalCurrentPriceMinor <= deal.HistoryLowAllMinor)));

        var total = await query.CountAsync(cancellationToken);
        var normalizedPage = GlobalDealCatalogPager.NormalizePage(page);
        var normalizedPageSize = GlobalDealCatalogPager.NormalizePageSize(pageSize);
        var skip = (long)(normalizedPage - 1) * normalizedPageSize;

        List<GlobalDealRow> rows = skip >= total
            ? []
            : await query
                .OrderByDescending(deal => deal.DiscountPercent ?? -1)
                .ThenByDescending(deal => deal.ObservedAt)
                .ThenBy(deal => deal.DealKey)
                .Skip((int)skip)
                .Take(normalizedPageSize)
                .Select(deal => new GlobalDealRow(
                    deal.DealKey,
                    deal.ItadGameId,
                    deal.GameTitle,
                    deal.GameSlug,
                    deal.GameType,
                    deal.GameMature,
                    deal.ShopId,
                    deal.ShopName,
                    deal.OriginalCurrency,
                    deal.OriginalCurrentPriceMinor,
                    deal.OriginalRegularPriceMinor,
                    deal.DiscountPercent,
                    deal.MxnCurrentPriceMinor,
                    deal.PricingType,
                    deal.FxRate,
                    deal.FxRateDate,
                    deal.FxSource,
                    deal.HistoryLowAllMinor,
                    deal.HistoryLowCurrency,
                    deal.DealUrl,
                    deal.ExpiresAt,
                    deal.ObservedAt))
                .ToListAsync(cancellationToken);

        var items = rows.Select(ToItem).ToList();
        var hasMore = skip + items.Count < total;
        return new GlobalDealCatalog(items, items.Count, total, normalizedPage, normalizedPageSize, hasMore);
    }

    private GlobalDealCatalogItem ToItem(GlobalDealRow row)
    {
        var reason = GlobalDealPolicy.GoodDealReason(
            row.GameType,
            row.PricingType,
            row.DiscountPercent,
            row.OriginalCurrentPriceMinor,
            row.OriginalCurrency,
            row.HistoryLowAllMinor,
            row.HistoryLowCurrency,
            settings.MinDiscountPercent);

        return new GlobalDealCatalogItem(
            row.DealKey,
            row.ItadGameId,
            row.GameTitle,
            row.GameSlug,
            row.GameType,
            row.GameMature,
            row.ShopId,
            row.ShopName,
            row.OriginalCurrency,
            row.OriginalCurrentPriceMinor,
            row.OriginalRegularPriceMinor,
            row.DiscountPercent,
            row.MxnCurrentPriceMinor,
            row.PricingType,
            row.FxRate,
            row.FxRateDate,
            row.FxSource,
            row.HistoryLowAllMinor,
            row.HistoryLowCurrency,
            reason == GlobalDealSignals.HistoryLow,
            reason,
            SafeHttpsUrl(row.DealUrl),
            row.ExpiresAt,
            row.ObservedAt);
    }

    /// <summary>
    /// Resolves the current user's wishlist identity through the catalog, never through the unused
    /// <c>user_library.itad_game_id</c>: wished Steam rows give the appid and the MX <c>steam_games</c>
    /// row gives the ITAD game id used for exact exclusion.
    /// </summary>
    private async Task<List<string>> ResolveWishlistItadIdsAsync(int userId, CancellationToken cancellationToken)
    {
        var libraryRows = await repository.Get<UserLibrary>()
            .Where(entry => entry.UserId == userId &&
                entry.Store == GlobalDealWishlist.SteamStore &&
                entry.State == GlobalDealWishlist.WishedState)
            .Select(entry => new GlobalDealWishlistLibraryRow(
                entry.UserId,
                entry.Store,
                entry.State,
                entry.StoreGameId))
            .ToListAsync(cancellationToken);

        var appIds = libraryRows
            .Select(row => GlobalDealWishlist.ParseAppId(row.StoreGameId))
            .Where(appId => appId > 0)
            .Distinct()
            .ToList();
        if (appIds.Count == 0)
        {
            return [];
        }

        var games = await repository.Get<SteamGame>()
            .Where(game => game.Region == GlobalDealWishlist.Region && appIds.Contains(game.AppId))
            .Select(game => new GlobalDealWishlistGameRow(game.AppId, game.Name, game.ItadGameId, game.Region))
            .ToListAsync(cancellationToken);

        return GlobalDealWishlist.ItadIds(GlobalDealWishlist.Resolve(userId, libraryRows, games)).ToList();
    }

    /// <summary>
    /// Revalidates a stored URL before it can reach a response. Only an absolute HTTPS URL survives; the
    /// value is otherwise returned verbatim (affiliate tag included), never rewritten.
    /// </summary>
    private static string? SafeHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? value
            : null;

    /// <summary>Raw projection of one persisted row, read before the derived reason is computed.</summary>
    private sealed record GlobalDealRow(
        string DealKey,
        string ItadGameId,
        string GameTitle,
        string? GameSlug,
        string? GameType,
        bool GameMature,
        string ShopId,
        string ShopName,
        string OriginalCurrency,
        int OriginalCurrentPriceMinor,
        int? OriginalRegularPriceMinor,
        int? DiscountPercent,
        int? MxnCurrentPriceMinor,
        string PricingType,
        decimal? FxRate,
        DateOnly? FxRateDate,
        string? FxSource,
        int? HistoryLowAllMinor,
        string? HistoryLowCurrency,
        string? DealUrl,
        DateTime? ExpiresAt,
        DateTime ObservedAt);
}
