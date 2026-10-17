using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// The equal-share rule for an external bundle tier, as a pure function so it can be tested without a
/// database. It only allocates a per-game figure when the tier is honestly divisible: the provider listed
/// the whole item set, the tier is not an addon, it has a single published price with its currency and at
/// least one plain game. A null price (including a build-your-own tier) is unknown, not zero, and emits no
/// figure; the same goes for an incomplete list. Nothing here is ever an offer.
/// </summary>
public static class BundleShare
{
    /// <summary>Only a plain game is a standalone-comparable item; DLC and packages never count.</summary>
    public const string ComparableItemType = "game";

    public const string OkStatus = "ok";
    public const string AddonStatus = "addon";
    public const string ItemsIncompleteStatus = "items_incomplete";
    public const string NoTierPriceStatus = "no_tier_price";
    public const string CurrencyUnknownStatus = "currency_unknown";
    public const string NoEligibleItemsStatus = "no_eligible_items";

    /// <summary>
    /// Reads one tier into its equal-share projection. The reason order mirrors the aggregate comparison in
    /// <c>SteamGameService.DeriveTierComparison</c> (incomplete, addon, no price) so a tier that is
    /// unallocatable there is unallocatable here for the same stated reason.
    /// </summary>
    public static BundleCatalogTierShare Allocate(SteamGameBundleTier tier)
    {
        var items = tier.Games ?? [];
        var eligibleGameCount = items.Count(item =>
            string.Equals(item.Type, ComparableItemType, StringComparison.OrdinalIgnoreCase));

        int? allocated;
        string status;
        if (!tier.ItemsComplete)
        {
            allocated = null;
            status = ItemsIncompleteStatus;
        }
        else if (tier.Addon)
        {
            allocated = null;
            status = AddonStatus;
        }
        else if (tier.PriceMinor is null)
        {
            allocated = null;
            status = NoTierPriceStatus;
        }
        else if (string.IsNullOrWhiteSpace(tier.Currency))
        {
            allocated = null;
            status = CurrencyUnknownStatus;
        }
        else if (eligibleGameCount == 0)
        {
            allocated = null;
            status = NoEligibleItemsStatus;
        }
        else
        {
            allocated = DivideEvenly(tier.PriceMinor.Value, eligibleGameCount);
            status = OkStatus;
        }

        return new BundleCatalogTierShare(
            tier.PriceMinor,
            tier.Currency,
            tier.Addon,
            tier.ItemsComplete,
            items.Count,
            eligibleGameCount,
            allocated,
            status,
            RequiresWholeTierPurchase: allocated is not null);
    }

    /// <summary>
    /// Whether the tier's equal share is strictly cheaper than a standalone price in the same currency.
    /// Returns <see langword="null"/> when the share is not allocatable, when the standalone price or
    /// currency is unknown, or when the currencies differ: an honest comparison is never fabricated.
    ///
    /// The comparison uses the exact division (<c>tierPrice / count &lt; standalone</c> is
    /// <c>tierPrice &lt; standalone * count</c>) and never the rounded display figure, so the rounding can
    /// neither invent nor hide a saving.
    /// </summary>
    public static bool? IsShareCheaperThanStandalone(
        BundleCatalogTierShare share,
        int? standaloneMinor,
        string? standaloneCurrency)
    {
        if (share.AllocationStatus != OkStatus ||
            share.PriceMinor is not { } tierPrice ||
            share.Currency is null ||
            share.EligibleGameCount <= 0)
        {
            return null;
        }

        if (standaloneMinor is not { } standalone || standalone < 0)
        {
            return null;
        }

        if (standaloneCurrency is null ||
            !string.Equals(share.Currency, standaloneCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return tierPrice < (long)standalone * share.EligibleGameCount;
    }

    private static int DivideEvenly(int priceMinor, int count) =>
        (int)Math.Round(priceMinor / (decimal)count, MidpointRounding.AwayFromZero);
}
