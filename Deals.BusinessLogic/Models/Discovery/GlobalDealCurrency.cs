namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>Pricing classification of a persisted deal. Mirrors the offer semantics of the repo.</summary>
public static class GlobalDealPricingTypes
{
    /// <summary>The provider quoted the deal in MXN: the amount is a regional price as-is.</summary>
    public const string Regional = "regional";

    /// <summary>The provider quoted a foreign currency and the latest persisted rate converted it.</summary>
    public const string FxEstimate = "fx_estimate";

    /// <summary>No MXN figure is claimed: the original price is shown with its own currency only.</summary>
    public const string Unconverted = "unconverted";
}

/// <summary>
/// The read-only FX rate used for one sync pass, adapted from the persisted <c>fx_rates</c> row. It
/// carries the source of the row so the catalog can show where the conversion came from.
/// </summary>
public sealed record GlobalDealFxRate(decimal Rate, DateOnly RateDate, string Source);

/// <summary>
/// The derived MXN projection of one deal. <see cref="PricingType"/> is the contract the UI trusts:
/// only <c>regional</c> and <c>fx_estimate</c> carry a usable MXN amount; <c>unconverted</c> is an
/// explicit "no MXN claim".
/// </summary>
public sealed record GlobalDealPriceProjection(
    int? MxnCurrentPriceMinor,
    decimal? FxRate,
    DateOnly? FxRateDate,
    string? FxSource,
    string PricingType);

/// <summary>
/// Pure currency projection for the deals feed. Only an exact MXN quote is regional; only an exact USD
/// quote with a positive persisted rate becomes an <c>fx_estimate</c>. Every other currency stays
/// <c>unconverted</c> with no MXN amount: inventing a cross rate or a zero would be a fabricated price.
/// </summary>
public static class GlobalDealCurrency
{
    public const string Mxn = "MXN";
    public const string Usd = "USD";

    public static GlobalDealPriceProjection Project(
        string originalCurrency,
        int originalCurrentPriceMinor,
        GlobalDealFxRate? usdToMxn)
    {
        if (string.Equals(originalCurrency, Mxn, StringComparison.Ordinal))
        {
            return new GlobalDealPriceProjection(
                originalCurrentPriceMinor,
                null,
                null,
                null,
                GlobalDealPricingTypes.Regional);
        }

        if (string.Equals(originalCurrency, Usd, StringComparison.Ordinal) &&
            usdToMxn is { Rate: > 0 } rate)
        {
            return new GlobalDealPriceProjection(
                ConvertMinor(originalCurrentPriceMinor, rate.Rate),
                rate.Rate,
                rate.RateDate,
                rate.Source,
                GlobalDealPricingTypes.FxEstimate);
        }

        return new GlobalDealPriceProjection(
            null,
            null,
            null,
            null,
            GlobalDealPricingTypes.Unconverted);
    }

    /// <summary>
    /// Multiplies minor units by the rate with the same away-from-zero rounding the offer pipeline
    /// uses. An overflowing or non-positive result is no conversion at all, never a wrapped number.
    /// </summary>
    public static int? ConvertMinor(int amountMinor, decimal rate)
    {
        if (amountMinor < 0 || rate <= 0)
        {
            return null;
        }

        var converted = amountMinor * rate;
        if (converted < 0 || converted > int.MaxValue)
        {
            return null;
        }

        return (int)Math.Round(converted, MidpointRounding.AwayFromZero);
    }
}
