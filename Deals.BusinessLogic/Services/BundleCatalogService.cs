using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Read-only catalog over the persisted ITAD bundles. It answers <c>GET /api/bundles</c> from rows that
/// were already observed and written by the game refresh and the global sweep: no provider call happens
/// here, no bundle is written, and no price read here enters <c>game_offers</c> or any savings math. The
/// existing aggregate tier savings in <c>SteamGameService</c> is untouched; this service only adds the
/// equal-share estimate the catalog and the game detail display.
///
/// The catalog is paged honestly and scoped to the authenticated user for the wishlist highlight: the
/// user id arrives from the controller (never from the query string) and is only used to resolve that
/// user's wished Steam rows. A bundle without any wishlist game is still returned in full.
///
/// Each tier also carries its USD/MXN reading (<see cref="BundleCurrencyProjection"/>). It comes from the
/// persisted <c>fx_rates</c> rows only — <see cref="IFxRateService.GetLatestRateAsync"/>, never the
/// provider-fetching member — and each pair is read at most once per request, not once per tier.
///
/// <para>
/// Bundles the current user already purchased are removed <b>before</b> deduplication and pagination
/// (spec S3), so the pending catalog's count, pages and order describe only what is still pending for that
/// user. The match is by source family and bundle key (<see cref="BundlePurchaseRules.HidesBundle"/>): an
/// <c>itad</c> purchase hides the <c>itad_global</c> snapshot of the same bundle, and gg.deals stays its own
/// key space.
/// </para>
/// </summary>
public sealed class BundleCatalogService(IRepository repository, IFxRateService fxRateService)
{
    /// <summary>Bound of the linked games listed per bundle.</summary>
    public const int MaxLinkedGamesPerBundle = 20;

    private const string ItadSource = BundleCatalogOrder.ItadSource;
    private const string ItadGlobalSource = BundleCatalogOrder.ItadGlobalSource;
    private const string GgDealsSource = BundleCatalogOrder.GgDealsSource;

    /// <summary>
    /// Every source the catalog reads. gg.deals is its own source with a client-owned key space, so a
    /// gg.deals bundle is never deduplicated against an ITAD one; it simply appears as another card.
    /// </summary>
    private static readonly string[] CatalogSources = [ItadSource, ItadGlobalSource, GgDealsSource];

    /// <summary>
    /// Same options the writer used (<c>SteamGameService.BundleTierJsonOptions</c>): web defaults give the
    /// camelCase names persisted in <c>tiers_json</c>.
    /// </summary>
    private static readonly JsonSerializerOptions TierJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One page of the active bundles from both ITAD sources, with their linked games, their per-tier
    /// equal share and the current user's exact wishlist matches. Expired bundles are excluded with the
    /// same rule the game detail uses (<c>ExpiresAt is null || ExpiresAt &gt; now</c>).
    /// </summary>
    public async Task<BundleCatalog> ListActiveAsync(
        int userId,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Step 1: lightweight identity only. Deduplicating here avoids loading every bundle's tier
        // payload just to discover which snapshot wins for a repeated bundle_key.
        var snapshots = await repository.Get<ExternalBundle>()
            .Where(bundle => CatalogSources.Contains(bundle.Source) &&
                (bundle.ExpiresAt == null || bundle.ExpiresAt > now))
            .Select(bundle => new BundleCatalogSnapshot(
                bundle.Source,
                bundle.BundleKey,
                bundle.Title,
                bundle.ExpiresAt,
                bundle.ObservedAt))
            .ToListAsync(cancellationToken);

        var deduplicated = BundleCatalogOrder.Deduplicate(snapshots);

        // Purchases are read before pagination on purpose: filtering a page would shrink it silently and
        // make Total disagree with the rows the caller can actually act on.
        var purchased = await repository.Get<BundlePurchase>()
            .Where(purchase => purchase.UserId == userId)
            .Select(purchase => new { purchase.Source, purchase.BundleKey })
            .ToListAsync(cancellationToken);
        var pending = deduplicated
            .Where(snapshot => !purchased.Any(purchase => BundlePurchaseRules.HidesBundle(
                purchase.Source,
                purchase.BundleKey,
                snapshot.Source,
                snapshot.BundleKey)))
            .ToList();

        var catalogPage = BundleCatalogPager.Paginate(pending, page, pageSize);
        var wishlist = await ResolveWishlistAsync(userId, cancellationToken);

        var items = new List<BundleCatalogItem>(catalogPage.Items.Count);
        if (catalogPage.Items.Count > 0)
        {
            var pageKeys = catalogPage.Items
                .Select(snapshot => snapshot.BundleKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Step 2: load the tiers of this page only.
            var rows = await repository.Get<ExternalBundle>()
                .Where(bundle => CatalogSources.Contains(bundle.Source) &&
                    (bundle.ExpiresAt == null || bundle.ExpiresAt > now) &&
                    pageKeys.Contains(bundle.BundleKey))
                .Select(bundle => new
                {
                    bundle.Source,
                    bundle.BundleKey,
                    bundle.Title,
                    bundle.ShopId,
                    bundle.ShopName,
                    bundle.PageUrl,
                    bundle.DealUrl,
                    bundle.Details,
                    bundle.PublishedAt,
                    bundle.ExpiresAt,
                    bundle.ObservedAt,
                    bundle.TiersJson,
                    LinkedGames = bundle.Links
                        .OrderBy(link => link.SteamGameId)
                        .Take(MaxLinkedGamesPerBundle)
                        .Select(link => new
                        {
                            link.SteamGame!.AppId,
                            link.SteamGame.Name,
                            link.SteamGame.ImageUrl
                        })
                        .ToList()
                })
                .ToListAsync(cancellationToken);

            var byKey = rows.ToDictionary(row => (row.Source, row.BundleKey));

            // The persisted pairs are read once for the whole page, before any tier is projected. Both
            // reads are read-only (never a provider fetch) and a bundle page without prices needs none.
            // Only the snapshots this page renders are parsed: a row that lost the deduplication keeps its
            // tier payload unread.
            var pageTiers = catalogPage.Items
                .Where(snapshot => byKey.ContainsKey((snapshot.Source, snapshot.BundleKey)))
                .ToDictionary(
                    snapshot => (snapshot.Source, snapshot.BundleKey),
                    snapshot => DeserializeTiers(byKey[(snapshot.Source, snapshot.BundleKey)].TiersJson));
            var fxRates = pageTiers.Values.Any(tiers => tiers.Any(tier => tier.PriceMinor is not null))
                ? await LoadFxRatesAsync(fxRateService, cancellationToken)
                : BundleFxRates.None;

            foreach (var snapshot in catalogPage.Items)
            {
                if (!byKey.TryGetValue((snapshot.Source, snapshot.BundleKey), out var row))
                {
                    // The row disappeared between the two reads; the page stays honest about its size.
                    continue;
                }

                var linkedGames = row.LinkedGames
                    .Where(game => game.AppId > 0 && !string.IsNullOrWhiteSpace(game.Name))
                    .Select(game => new BundleCatalogMember(game.AppId, game.Name, SafeHttpsUrl(game.ImageUrl)))
                    .ToList();
                var tiers = pageTiers[(row.Source, row.BundleKey)];

                items.Add(new BundleCatalogItem(
                    row.Source,
                    row.BundleKey,
                    row.Title,
                    row.ShopId,
                    row.ShopName,
                    SafeHttpsUrl(row.PageUrl),
                    SafeHttpsUrl(row.DealUrl),
                    row.Details,
                    row.PublishedAt,
                    row.ExpiresAt,
                    row.ObservedAt,
                    linkedGames,
                    BundleCurrencyProjection.ApplyAll(tiers, fxRates),
                    BundleWishlist.Match(wishlist, tiers, linkedGames)));
            }
        }

        return new BundleCatalog(
            items,
            items.Count,
            catalogPage.Total,
            catalogPage.Page,
            catalogPage.PageSize,
            catalogPage.HasMore);
    }

    /// <summary>
    /// Loads the two FX pairs a catalog page may need, exactly once each and read-only: the forward
    /// USD/MXN row prices a USD tier and the explicit MXN/USD row prices an MXN tier. The inverse is never
    /// derived from the other row — a rate the persistence never saw is a fabricated number. A stored rate
    /// that is not strictly positive is treated as no rate at all.
    ///
    /// Public so <c>checks/BundleCatalog.Checks</c> can count the reads without a database.
    /// </summary>
    public static async Task<BundleFxRates> LoadFxRatesAsync(
        IFxRateService fxRateService,
        CancellationToken cancellationToken)
    {
        var usdToMxn = await fxRateService.GetLatestRateAsync(
            GlobalDealCurrency.Usd,
            GlobalDealCurrency.Mxn,
            cancellationToken);
        var mxnToUsd = await fxRateService.GetLatestRateAsync(
            GlobalDealCurrency.Mxn,
            GlobalDealCurrency.Usd,
            cancellationToken);

        return new BundleFxRates(Usable(usdToMxn), Usable(mxnToUsd));
    }

    /// <summary>A pair is usable only with a strictly positive persisted rate.</summary>
    private static GlobalDealFxRate? Usable(FxRate? row) =>
        row is { Rate: > 0 }
            ? new GlobalDealFxRate(row.Rate, row.RateDate, row.Source)
            : null;

    /// <summary>
    /// Resolves the current user's wishlist identity through the catalog, never through the unused
    /// <c>user_library.itad_game_id</c>: wished Steam rows give the appid, the MX <c>steam_games</c> row
    /// gives the name and the ITAD game id.
    /// </summary>
    private async Task<IReadOnlyList<BundleWishlistEntry>> ResolveWishlistAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        var libraryRows = await repository.Get<UserLibrary>()
            .Where(entry => entry.UserId == userId &&
                entry.Store == BundleWishlist.SteamStore &&
                entry.State == BundleWishlist.WishedState)
            .Select(entry => new BundleWishlistLibraryRow(entry.UserId, entry.Store, entry.State, entry.StoreGameId))
            .ToListAsync(cancellationToken);

        var appIds = libraryRows
            .Select(row => BundleWishlist.ParseAppId(row.StoreGameId))
            .Where(appId => appId > 0)
            .Distinct()
            .ToList();
        if (appIds.Count == 0)
        {
            return [];
        }

        var games = await repository.Get<SteamGame>()
            .Where(game => game.Region == BundleWishlist.Region && appIds.Contains(game.AppId))
            .Select(game => new BundleWishlistGameRow(game.AppId, game.Name, game.ItadGameId, game.Region))
            .ToListAsync(cancellationToken);

        return BundleWishlist.Resolve(userId, libraryRows, games);
    }

    /// <summary>
    /// Reads the persisted tier payload. It was written by <c>SteamGameService.SerializeTiers</c>; anything
    /// that does not parse degrades to "no tiers" instead of failing the whole catalog read.
    /// </summary>
    private static IReadOnlyList<SteamGameBundleTier> DeserializeTiers(string? tiersJson)
    {
        if (string.IsNullOrWhiteSpace(tiersJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SteamGameBundleTier>>(tiersJson, TierJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Revalidates a stored URL before it can reach a response. Only an absolute HTTPS URL survives; the
    /// value is otherwise returned verbatim (affiliate tag included), never rewritten.
    /// </summary>
    private static string? SafeHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? value
            : null;
}
