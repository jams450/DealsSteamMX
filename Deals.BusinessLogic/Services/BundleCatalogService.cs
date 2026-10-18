using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
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
/// </summary>
public sealed class BundleCatalogService(IRepository repository)
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
        var catalogPage = BundleCatalogPager.Paginate(deduplicated, page, pageSize);
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
                var tiers = DeserializeTiers(row.TiersJson);

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
                    tiers.Select(BundleShare.Allocate).ToList(),
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
