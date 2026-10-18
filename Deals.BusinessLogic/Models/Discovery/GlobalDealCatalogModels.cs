namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>One <c>user_library</c> row projected for wishlist resolution (current user only).</summary>
public sealed record GlobalDealWishlistLibraryRow(
    int UserId,
    string Store,
    string State,
    string StoreGameId);

/// <summary>
/// One <c>steam_games</c> row projected for wishlist resolution. The ITAD id is read from here, never
/// from <c>user_library.itad_game_id</c>, because that column is not maintained and would be a stale
/// identity (the same measured trap the bundle catalog documents).
/// </summary>
public sealed record GlobalDealWishlistGameRow(
    int AppId,
    string Name,
    string? ItadGameId,
    string Region);

/// <summary>The resolved identity of one wished game: appid, catalog name and MX ITAD game id.</summary>
public sealed record GlobalDealWishlistEntry(
    int AppId,
    string Name,
    string? ItadGameId);

/// <summary>
/// The wishlist rule of the deals feed as pure functions, so it is exercised without a database. The
/// feed is "good deals for games the user has not wishlisted yet": a deal whose ITAD game id is in the
/// current user's resolved wishlist is excluded. Resolution goes <c>user_library</c> (store steam,
/// state wished) to Steam appid to the MX <c>steam_games</c> row to its ITAD game id; a row of another
/// user, store or state is dropped, and a game without an ITAD identity can never match.
/// </summary>
public static class GlobalDealWishlist
{
    public const string SteamStore = "steam";
    public const string WishedState = "wished";
    public const string Region = "mx";

    public static IReadOnlyList<GlobalDealWishlistEntry> Resolve(
        int currentUserId,
        IEnumerable<GlobalDealWishlistLibraryRow> libraryRows,
        IEnumerable<GlobalDealWishlistGameRow> games)
    {
        var appIds = libraryRows
            .Where(row => row.UserId == currentUserId &&
                string.Equals(row.Store, SteamStore, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.State, WishedState, StringComparison.OrdinalIgnoreCase))
            .Select(row => ParseAppId(row.StoreGameId))
            .Where(appId => appId > 0)
            .ToHashSet();

        if (appIds.Count == 0)
        {
            return [];
        }

        var byAppId = games
            .Where(game => string.Equals(game.Region, Region, StringComparison.OrdinalIgnoreCase))
            .GroupBy(game => game.AppId)
            .ToDictionary(group => group.Key, group => group.First());

        return appIds
            .Where(byAppId.ContainsKey)
            .OrderBy(appId => appId)
            .Select(appId => new GlobalDealWishlistEntry(appId, byAppId[appId].Name, byAppId[appId].ItadGameId))
            .ToList();
    }

    /// <summary>The distinct ITAD game ids of the resolved wishlist, for exact exclusion.</summary>
    public static IReadOnlySet<string> ItadIds(IEnumerable<GlobalDealWishlistEntry> entries) =>
        entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ItadGameId))
            .Select(entry => entry.ItadGameId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the deal is outside the resolved wishlist. A deal with no ITAD identity cannot be
    /// proven to be wished, so it stays visible rather than being hidden by a guess.
    /// </summary>
    public static bool IsOutsideWishlist(string? itadGameId, IReadOnlySet<string> wishedItadIds) =>
        string.IsNullOrWhiteSpace(itadGameId) || !wishedItadIds.Contains(itadGameId);

    public static int ParseAppId(string storeGameId) =>
        int.TryParse(storeGameId, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var appId)
            ? appId
            : 0;
}

/// <summary>
/// One persisted good deal as the catalog page reads it. <see cref="Currency"/> is always the
/// provider's original currency; <see cref="MxnCurrentPriceMinor"/> is null unless
/// <see cref="PricingType"/> is regional or fx_estimate, so the UI never has to guess.
/// </summary>
public sealed record GlobalDealCatalogItem(
    string DealKey,
    string ItadGameId,
    string GameTitle,
    string? GameSlug,
    string? GameType,
    bool GameMature,
    string ShopId,
    string ShopName,
    string Currency,
    int CurrentPriceMinor,
    int? RegularPriceMinor,
    int? DiscountPercent,
    int? MxnCurrentPriceMinor,
    string PricingType,
    decimal? FxRate,
    DateOnly? FxRateDate,
    string? FxSource,
    int? HistoryLowMinor,
    string? HistoryLowCurrency,
    bool IsAtHistoryLow,
    string? GoodDealReason,
    string? DealUrl,
    DateTime? ExpiresAt,
    DateTime ObservedAt);

/// <summary>
/// One page of the good-deals feed. <see cref="Count"/> is how many deals this page returned and
/// <see cref="Total"/> is how many good deals outside the wishlist exist in the whole catalog.
/// </summary>
public sealed record GlobalDealCatalog(
    IReadOnlyList<GlobalDealCatalogItem> Items,
    int Count,
    int Total,
    int Page,
    int PageSize,
    bool HasMore);

/// <summary>Settings the catalog read uses. The threshold is a local policy, not a provider claim.</summary>
public sealed record GlobalDealCatalogSettings(int MinDiscountPercent);

/// <summary>
/// Pagination bounds shared by the controller and the service. Page numbers are 1-based; an invalid
/// page is page 1 and an oversized page size is clamped.
/// </summary>
public static class GlobalDealCatalogPager
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxPage = 1_000_000;

    public static int NormalizePage(int? page) => page is null or < 1 ? 1 : Math.Min(page.Value, MaxPage);

    public static int NormalizePageSize(int? pageSize) =>
        pageSize is null or < 1 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
}
