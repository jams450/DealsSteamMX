namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>
/// Why a persisted deal is surfaced as a good deal. These are codes, not a rating: the UI copies them
/// to text. There is deliberately no numeric score, because <c>deals/v2</c> publishes no score and
/// manufacturing one from a single discount would be a fabricated quality signal.
/// </summary>
public static class GlobalDealSignals
{
    /// <summary>The documented discount (<c>cut</c>, or regular-vs-current arithmetic) cleared the policy.</summary>
    public const string Discount = "discount";

    /// <summary>The current price is at or below a provider-reported history low in the same currency.</summary>
    public const string HistoryLow = "history_low";
}

/// <summary>
/// Pure eligibility and "good deal" policy for the global deals feed. It uses only fields the provider
/// actually reported:
///   - the item is a base game (<c>type == "game"</c>); dlc/package/null are never part of the games feed;
///   - an MXN price is present (regional or converted; a foreign unconverted amount is never eligible);
///   - the discount, derived from the documented <c>cut</c> or from regular-vs-current arithmetic when
///     both are present, clears the configured threshold; or
///   - the current original price is at or below a provider-reported history low quoted in the same
///     currency as the price. A low is never inferred from our own observations and no rating is
///     asserted.
/// </summary>
public static class GlobalDealPolicy
{
    /// <summary>Local policy default, not a provider claim: surface discounts of at least this percent.</summary>
    public const int DefaultMinDiscountPercent = 50;

    /// <summary>True only for the two pricing types that carry a usable MXN amount.</summary>
    public static bool IsPricingComparable(string? pricingType) =>
        pricingType is GlobalDealPricingTypes.Regional or GlobalDealPricingTypes.FxEstimate;

    /// <summary>
    /// True when the original price is at or below a provider-reported low, quoted in the same currency
    /// as the price. Both values must be actual reported fields; a missing low never matches.
    /// </summary>
    public static bool IsAtHistoryLow(
        int originalCurrentPriceMinor,
        string? originalCurrency,
        int? historyLowMinor,
        string? historyLowCurrency) =>
        historyLowMinor is > 0 &&
        !string.IsNullOrWhiteSpace(historyLowCurrency) &&
        string.Equals(historyLowCurrency, originalCurrency, StringComparison.Ordinal) &&
        originalCurrentPriceMinor > 0 &&
        originalCurrentPriceMinor <= historyLowMinor;

    /// <summary>True only for the base-game type. The contract's <c>type</c> is <c>game</c>,
    /// <c>dlc</c>, <c>package</c> or null; the feed is a games feed.</summary>
    public static bool IsEligibleGameType(string? gameType) =>
        string.Equals(gameType, "game", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The reason the deal qualifies, or null when it does not. History low is checked first because it
    /// is the strongest evidence; a discount is only reported as such when it clears the threshold.
    /// </summary>
    public static string? GoodDealReason(
        string? gameType,
        string? pricingType,
        int? discountPercent,
        int originalCurrentPriceMinor,
        string? originalCurrency,
        int? historyLowMinor,
        string? historyLowCurrency,
        int minDiscountPercent)
    {
        if (!IsEligibleGameType(gameType) || !IsPricingComparable(pricingType))
        {
            return null;
        }

        if (IsAtHistoryLow(originalCurrentPriceMinor, originalCurrency, historyLowMinor, historyLowCurrency))
        {
            return GlobalDealSignals.HistoryLow;
        }

        if (discountPercent is { } discount && discount >= minDiscountPercent)
        {
            return GlobalDealSignals.Discount;
        }

        return null;
    }

    /// <summary>
    /// Discount from the documented <c>cut</c> when it is coherent, otherwise from regular-vs-current
    /// arithmetic when both amounts are present. Returns null when neither is available: no discount is
    /// invented.
    /// </summary>
    public static int? DiscountPercent(int? cut, int currentPriceMinor, int? regularPriceMinor)
    {
        if (cut is >= 0 and <= 100)
        {
            return cut;
        }

        if (regularPriceMinor is not > 0 || currentPriceMinor < 0 || currentPriceMinor > regularPriceMinor)
        {
            return null;
        }

        return (int)Math.Round(
            (regularPriceMinor.Value - currentPriceMinor) * 100d / regularPriceMinor.Value,
            MidpointRounding.AwayFromZero);
    }
}
