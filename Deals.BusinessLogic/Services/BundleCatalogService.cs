using System.Text.Json;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Steam;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Read-only catalog over the persisted ITAD bundles. It answers <c>GET /api/bundles</c> from rows that
/// were already observed and written by the game refresh: no provider call happens here, no bundle is
/// written, and no price read here enters <c>game_offers</c> or any savings math. The existing aggregate
/// tier savings in <c>SteamGameService</c> is untouched; this service only adds the equal-share estimate
/// the catalog and the game detail display.
/// </summary>
public sealed class BundleCatalogService(IRepository repository)
{
    /// <summary>Hard bound of one read. A larger catalog is truncated, never paged into an unbounded query.</summary>
    public const int MaxBundles = 100;

    /// <summary>Bound of the linked games listed per bundle.</summary>
    public const int MaxLinkedGamesPerBundle = 20;

    private const string ItadSource = "itad";

    /// <summary>
    /// Same options the writer used (<c>SteamGameService.BundleTierJsonOptions</c>): web defaults give the
    /// camelCase names persisted in <c>tiers_json</c>.
    /// </summary>
    private static readonly JsonSerializerOptions TierJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The active ITAD bundles with their linked games and their per-tier equal share. ITAD only: another
    /// source's bundles are outside this catalog. Expired bundles are excluded with the same rule the game
    /// detail uses (<c>ExpiresAt is null || ExpiresAt &gt; now</c>).
    /// </summary>
    public async Task<BundleCatalog> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var rows = await repository.Get<ExternalBundle>()
            .Where(bundle => bundle.Source == ItadSource &&
                (bundle.ExpiresAt == null || bundle.ExpiresAt > now))
            // Soonest to expire first (null = no date, last), then title for a stable order across reads.
            .OrderBy(bundle => bundle.ExpiresAt == null)
            .ThenBy(bundle => bundle.ExpiresAt)
            .ThenBy(bundle => bundle.Title)
            .ThenBy(bundle => bundle.ExternalBundleId)
            .Take(MaxBundles)
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

        var items = rows.Select(row => new BundleCatalogItem(
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
            row.LinkedGames
                .Where(game => game.AppId > 0 && !string.IsNullOrWhiteSpace(game.Name))
                .Select(game => new BundleCatalogMember(game.AppId, game.Name, SafeHttpsUrl(game.ImageUrl)))
                .ToList(),
            DeserializeTiers(row.TiersJson)
                .Select(BundleShare.Allocate)
                .ToList())).ToList();

        return new BundleCatalog(items, items.Count, MaxBundles);
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
