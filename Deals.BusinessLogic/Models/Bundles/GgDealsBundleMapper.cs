using Deals.BusinessLogic.Models.GgDeals;
using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// Maps sanitized gg.deals active bundles into the persisted display shape. GG.deals documents no
/// identity beyond a nullable article URL, so nothing here is invented: the heuristic key is computed
/// by the client and the item URLs are the only per-item reference. GG.deals tiers have no ITAD UUID
/// and no linked Steam appid, so a gg.deals bundle can never claim a wishlist match or a per-game
/// cheaper price by title — the honest consequence of the missing identity.
///
/// The selection count (<c>gamesCount</c>) is preserved and bounded: a count outside the listed set is
/// unsatisfiable, so the tier is persisted as incomplete and earns no per-game share.
/// </summary>
public static class GgDealsBundleMapper
{
    public const int MaxTiersPerBundle = 20;
    public const int MaxGamesPerTier = 50;

    /// <summary>A gg.deals bundle item is a game; the provider documents no DLC/package typing.</summary>
    public const string ItemType = "game";

    public static SteamGameBundle Map(GgDealsBundle bundle, DateTime observedAt)
    {
        var tiers = new List<SteamGameBundleTier>(Math.Min(bundle.Tiers.Count, MaxTiersPerBundle));
        foreach (var tier in bundle.Tiers.Take(MaxTiersPerBundle))
        {
            var truncated = tier.Games.Count > MaxGamesPerTier;
            var selectionCount = tier.GamesCount is { } count && count > 0 && count <= tier.Games.Count
                ? count
                : (int?)null;

            var games = tier.Games
                .Take(MaxGamesPerTier)
                .Select(game => new SteamGameBundleTierGame(
                    game.Title,
                    ItemType,
                    null,
                    null,
                    ItadGameId: null,
                    Url: SafeHttpsUrl(game.Url)))
                .ToList();

            tiers.Add(new SteamGameBundleTier(
                tier.PriceMinor,
                tier.Currency,
                Addon: false,
                // The provider listed every item it has for this page; a persistence truncation is the
                // only incompleteness. An unsatisfiable gamesCount is also persisted as incomplete.
                ItemsComplete: !truncated && (tier.GamesCount is null || selectionCount is not null),
                games,
                SelectionCount: selectionCount));
        }

        return new SteamGameBundle(
            BundleCatalogOrder.GgDealsSource,
            bundle.BundleKey,
            bundle.Title,
            ShopId: "gg.deals",
            ShopName: "GG.deals",
            PageUrl: SafeHttpsUrl(bundle.Url),
            DealUrl: null,
            Details: null,
            PublishedAt: bundle.DateFrom,
            ExpiresAt: bundle.DateTo,
            ObservedAt: observedAt,
            tiers);
    }

    /// <summary>
    /// Items survive only as an absolute HTTPS URL; anything else is stored as null. The provider's
    /// article URL is nullable by contract, so null is a valid value here, not an error.
    /// </summary>
    private static string? SafeHttpsUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? value
            : null;
}
