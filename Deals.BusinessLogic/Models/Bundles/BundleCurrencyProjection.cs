using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Steam;

namespace Deals.BusinessLogic.Models.Bundles;

/// <summary>
/// The dual-currency reading of one tier amount (shared tier contract L4). <see cref="UsdMinor"/> and
/// <see cref="MxnMinor"/> are minor units of the same amount seen from both currencies; the side that
/// matches the tier's own currency is the published figure and the other one is the estimate. A missing
/// currency stays <see langword="null"/> — an unpriced side is never written as 0, and a published zero
/// stays 0 on both sides only when the persisted rate justifies it.
///
/// <see cref="FxRate"/>, <see cref="FxRateDate"/> and <see cref="FxSource"/> are exactly the persisted
/// <c>fx_rates</c> row that produced the converted side, and are all null when no conversion happened.
/// <see cref="PricingType"/> uses the repo's existing vocabulary
/// (<see cref="GlobalDealPricingTypes"/>): <c>fx_estimate</c> when one side was derived from a persisted
/// rate, <c>regional</c> when the amount is already MXN and no rate was available, and
/// <c>unconverted</c> when the original currency is not MXN and no honest MXN figure can be claimed
/// (unknown price, unsupported currency, unusable or overflowing rate).
/// </summary>
public sealed record BundleMoney(
    int? UsdMinor,
    int? MxnMinor,
    decimal? FxRate,
    DateOnly? FxRateDate,
    string? FxSource,
    string PricingType);

/// <summary>
/// The two persisted FX pairs one catalog request may use, loaded once and shared by every tier of the
/// page. <see cref="UsdToMxn"/> prices a USD tier and <see cref="MxnToUsd"/> prices an MXN tier; neither
/// stands in for the other, because inventing an inverse rate would be a fabricated number the
/// persistence never saw. <see cref="None"/> is the honest "no persisted pair" reading.
/// </summary>
public sealed record BundleFxRates(
    GlobalDealFxRate? UsdToMxn,
    GlobalDealFxRate? MxnToUsd)
{
    public static BundleFxRates None { get; } = new(null, null);
}

/// <summary>
/// Pure dual-currency projection of a bundle tier. It is deliberately a function of already-loaded data:
/// it takes no service, performs no read and can never fan out to a provider, so a page of tiers costs
/// exactly the two persisted rate reads the caller made once. The native amount always survives
/// untouched (the existing <c>priceMinor</c>/<c>currency</c> and <c>allocatedPerGameMinor</c> fields keep
/// their values); this only adds the USD/MXN block.
///
/// The allocated block converts the tier total first and then divides the converted total by the same
/// denominator the untouched equal share used (<c>selectionCount</c> when the provider published a
/// "choose N of M", else the eligible-game count). Converting the rounded share instead would report a
/// different figure (1001 minor at rate 2 over 3 games: 667 by the honest route, 668 by the shortcut).
/// </summary>
public static class BundleCurrencyProjection
{
    private static readonly BundleMoney NoFigure =
        new(null, null, null, null, null, GlobalDealPricingTypes.Unconverted);

    /// <summary>
    /// Adds both money blocks to one already-allocated tier. The tier's own fields are copied verbatim;
    /// only <see cref="BundleCatalogTierShare.PriceMoney"/> and
    /// <see cref="BundleCatalogTierShare.AllocatedPerGameMoney"/> are filled.
    /// </summary>
    public static BundleCatalogTierShare Apply(BundleCatalogTierShare share, BundleFxRates rates) =>
        share with
        {
            PriceMoney = ForAmount(share.PriceMinor, share.Currency, rates),
            AllocatedPerGameMoney = ForAllocation(share, rates)
        };

    /// <summary>
    /// Projects every tier of a page from the same already-loaded pairs.
    /// </summary>
    public static IReadOnlyList<BundleCatalogTierShare> ApplyAll(
        IEnumerable<SteamGameBundleTier> tiers,
        BundleFxRates rates) =>
        tiers.Select(tier => Apply(BundleShare.Allocate(tier), rates)).ToList();

    /// <summary>
    /// Reads one native amount in both currencies. A null amount is unknown, not free; a currency that is
    /// neither USD nor MXN keeps no USD/MXN claim (its native figure is the untouched tier field).
    /// </summary>
    public static BundleMoney ForAmount(int? amountMinor, string? currency, BundleFxRates rates)
    {
        if (amountMinor is not { } amount)
        {
            return NoFigure;
        }

        var code = NormalizeCurrency(currency);
        if (string.Equals(code, GlobalDealCurrency.Usd, StringComparison.Ordinal))
        {
            return Converted(amount, null, rates.UsdToMxn, GlobalDealPricingTypes.Unconverted);
        }

        if (string.Equals(code, GlobalDealCurrency.Mxn, StringComparison.Ordinal))
        {
            return Converted(null, amount, rates.MxnToUsd, GlobalDealPricingTypes.Regional);
        }

        return NoFigure;
    }

    /// <summary>
    /// Reads the equal-share estimate of one tier. Only a tier whose allocation is possible
    /// (<see cref="BundleShare.OkStatus"/>: complete item list, published price, not an addon, at least
    /// one eligible game) emits a block; every other reason emits <see langword="null"/> and the existing
    /// <c>allocationStatus</c> already explains why.
    /// </summary>
    public static BundleMoney? ForAllocation(BundleCatalogTierShare share, BundleFxRates rates)
    {
        if (share.AllocationStatus != BundleShare.OkStatus ||
            share.AllocatedPerGameMinor is not { } allocated ||
            share.PriceMinor is not { } totalMinor)
        {
            return null;
        }

        // The same denominator BundleShare used, so the converted share is the converted total divided
        // the way the native share already was.
        var denominator = share.SelectionCount ?? share.EligibleGameCount;
        if (denominator <= 0)
        {
            return null;
        }

        var code = NormalizeCurrency(share.Currency);
        if (string.Equals(code, GlobalDealCurrency.Usd, StringComparison.Ordinal))
        {
            return Converted(allocated, null, rates.UsdToMxn, GlobalDealPricingTypes.Unconverted, totalMinor, denominator);
        }

        if (string.Equals(code, GlobalDealCurrency.Mxn, StringComparison.Ordinal))
        {
            return Converted(null, allocated, rates.MxnToUsd, GlobalDealPricingTypes.Regional, totalMinor, denominator);
        }

        return NoFigure;
    }

    /// <summary>
    /// Builds the block from the native side(s) and the one rate that may complete it. Without a usable
    /// rate the block keeps only the native figure and the caller's fallback pricing type; with a rate it
    /// reports the converted side and the exact row that produced it.
    ///
    /// When <paramref name="allocationTotalMinor"/> is present the converted side is the converted total
    /// divided evenly (the equal-share rule), never a conversion of the already-rounded share.
    /// </summary>
    private static BundleMoney Converted(
        int? nativeUsdMinor,
        int? nativeMxnMinor,
        GlobalDealFxRate? rate,
        string withoutRatePricingType,
        int? allocationTotalMinor = null,
        int? denominator = null)
    {
        if (rate is not { Rate: > 0 } usable)
        {
            return new BundleMoney(
                nativeUsdMinor,
                nativeMxnMinor,
                null,
                null,
                null,
                withoutRatePricingType);
        }

        if (allocationTotalMinor is { } total && denominator is { } count)
        {
            var convertedTotal = GlobalDealCurrency.ConvertMinor(total, usable.Rate);
            if (convertedTotal is not { } totalMinor)
            {
                return new BundleMoney(nativeUsdMinor, nativeMxnMinor, null, null, null, withoutRatePricingType);
            }

            var convertedShare = DivideEvenly(totalMinor, count);
            return new BundleMoney(
                nativeUsdMinor ?? convertedShare,
                nativeMxnMinor ?? convertedShare,
                usable.Rate,
                usable.RateDate,
                usable.Source,
                GlobalDealPricingTypes.FxEstimate);
        }

        var native = nativeUsdMinor ?? nativeMxnMinor;
        if (native is not { } amount)
        {
            return NoFigure;
        }

        var converted = GlobalDealCurrency.ConvertMinor(amount, usable.Rate);
        if (converted is not { } figure)
        {
            // Overflow or a non-positive result is no conversion at all: the native figure stays and the
            // other currency is unknown, never a wrapped or negative amount.
            return new BundleMoney(nativeUsdMinor, nativeMxnMinor, null, null, null, withoutRatePricingType);
        }

        return new BundleMoney(
            nativeUsdMinor ?? figure,
            nativeMxnMinor ?? figure,
            usable.Rate,
            usable.RateDate,
            usable.Source,
            GlobalDealPricingTypes.FxEstimate);
    }

    /// <summary>
    /// The equal-share rounding rule of <see cref="BundleShare"/>, reused so both currencies round the
    /// same way. The input is already range-checked and the count is at least 1, so the result fits.
    /// </summary>
    private static int DivideEvenly(int amountMinor, int count) =>
        (int)Math.Round(amountMinor / (decimal)count, MidpointRounding.AwayFromZero);

    private static string NormalizeCurrency(string? currency) =>
        currency?.Trim().ToUpperInvariant() ?? string.Empty;
}
