using Deals.BusinessLogic.Models.Itad;
using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// Maps sanitized ITAD tiers into the persisted display shape. Two provider facts are preserved because
/// they are what later reads need: the item's ITAD UUID (so a read can prove, by exact id and never by
/// title, which tier lists the queried game) and the item's current price (so the aggregate comparison
/// stays honest). Both lists are capped and a persistence-truncated item list is marked incomplete, which
/// permanently disqualifies that tier from any saving or share comparison.
/// </summary>
public static class BundleTierMapper
{
    public static IReadOnlyList<SteamGameBundleTier> Map(
        IReadOnlyList<ItadBundleTier> tiers,
        int maxTiers,
        int maxGamesPerTier)
    {
        var mapped = new List<SteamGameBundleTier>(Math.Min(tiers.Count, maxTiers));
        foreach (var tier in tiers.Take(maxTiers))
        {
            var games = new List<SteamGameBundleTierGame>(Math.Min(tier.Games.Count, maxGamesPerTier));
            foreach (var game in tier.Games.Take(maxGamesPerTier))
            {
                games.Add(new SteamGameBundleTierGame(
                    game.Title,
                    game.Type,
                    game.CurrentPriceMinor,
                    game.PriceCurrency,
                    game.Id));
            }

            mapped.Add(new SteamGameBundleTier(
                tier.PriceMinor,
                tier.Currency,
                tier.Addon,
                // The persistence cap is a truncation too: a tier summed over fewer items than its own
                // list is a partial total, so it must be persisted as incomplete.
                tier.ItemsComplete && tier.Games.Count <= maxGamesPerTier,
                games));
        }

        return mapped;
    }
}
