using System.Globalization;
using Deals.BusinessLogic.Models.Notifications;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Pure, deterministic policy for the global ntfy deal alerts. No DB, no HTTP, no clock of its own:
/// the caller supplies the snapshot and the evaluation instant. That is what makes every rule testable.
///
/// A candidate is a single fresh, MX-region <c>game_offers</c> row (see <see cref="DealAlertCandidate"/>) or the
/// game's MX Steam direct snapshot. It fires when ANY of three independent rules is true (spec S2/S4):
///   1. the wishlist deal score is strictly greater than 7, using the exact 0-10 formula of
///      <c>Deals.Web/app/wishlist/_lib/wishlist-metrics.ts</c> (<see cref="DealScore"/>);
///   2. the MXN price is strictly below <see cref="CheapPriceThresholdMinor"/> (50.00 MXN);
///   3. the offer is at a verified provider history low (see <see cref="HasVerifiedHistoryLow"/>).
///
/// The three rules are independent OR conditions and none is a ceiling for another: a 150.00 MXN offer can
/// fire rule 1, and a 60.00 MXN offer can fire rule 3. That is also why the discover catalog exposes them as
/// three separate categories (`under50`, `historic`, `score`) instead of one "good deal" bucket: the same
/// policy, the same three reasons, read from persisted snapshots.
///
/// The history-low rule is intentionally narrow. "Verified" means the low and the current price are
/// quoted by the same provider row in the same currency, so no FX conversion and no cross-source
/// comparison can manufacture a low. A locally observed Steam minimum is never evidence here.
/// </summary>
public static class DealAlertPolicy
{
    /// <summary>
    /// Policy generation folded into the dedup key. Bumping it intentionally re-alerts every currently
    /// qualifying offer exactly once; changing behaviour without bumping it does not.
    /// </summary>
    public const int TriggerVersion = 1;

    /// <summary>Rule 1: the score must exceed this value. 7.0 does NOT fire; 7.1 does.</summary>
    public const double ScoreTriggerThreshold = 7d;

    /// <summary>
    /// Rule 2: the MXN price must be strictly below this many minor units (50.00 MXN). 4999 fires, 5000 does
    /// not. Shared verbatim with the discover catalog's <c>under50</c> category, so a notification and a tab
    /// can never disagree about what "under 50" means.
    /// </summary>
    public const int CheapPriceThresholdMinor = 5_000;

    /// <summary>Above this the amount is corrupt data, not a price (5,000,000.00 MXN).</summary>
    public const int MaxPriceMinor = 500_000_000;

    /// <summary>
    /// The one region this product compares and alerts on, and the case convention that goes with it: the value
    /// is lowercase <c>mx</c> and the comparison is ordinal, exactly like the catalog's SQL
    /// (<c>CatalogDealQuery.CanonicalOffer</c> renders <c>region = 'mx'</c>) and like every ingestion client that
    /// writes <c>game_offers.region</c>/<c>steam_games.region</c>. So <c>MX</c>, <c> mx</c> and <c>mexico</c> are
    /// not MX here, and a row stored that way has to be fixed at the source instead of being silently matched by
    /// a case-insensitive policy the catalog would contradict.
    /// </summary>
    public const string Region = "mx";

    private const string MxnCurrency = "MXN";
    private const string SteamSource = "steam";
    private const string UnconvertedPricing = "unconverted";
    private const string EpicSource = "epic";

    /// <summary>DealScore construction constants, copied from the frontend formula.</summary>
    private const double ScoreCeiling = 90d;

    /// <summary>Evaluates one candidate. Never throws on input data; a bad row simply does not fire.</summary>
    public static DealAlertDecision Evaluate(DealAlertCandidate candidate, DealAlertContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var key = DedupKey(candidate);
        if (!IsComparable(candidate, context))
        {
            return new DealAlertDecision(false, candidate, [], null, key, null);
        }

        var score = DealScore(
            candidate.BasePriceMinor,
            candidate.BaseCurrency,
            candidate.MxnCurrentPriceMinor,
            candidate.MxnHistoryLowMinor,
            candidate.MxnHistoryLowCurrency,
            context.MinViableDiscountPercent);

        var reasons = new List<string>(3);
        if (candidate.MxnCurrentPriceMinor < CheapPriceThresholdMinor)
        {
            reasons.Add(DealAlertReasons.Price);
        }

        if (score is > ScoreTriggerThreshold)
        {
            reasons.Add(DealAlertReasons.Score);
        }

        if (HasVerifiedHistoryLow(candidate))
        {
            reasons.Add(DealAlertReasons.HistoryLow);
        }

        if (reasons.Count == 0)
        {
            return new DealAlertDecision(false, candidate, [], score, key, null);
        }

        return new DealAlertDecision(true, candidate, reasons, score, key, BuildMessage(candidate, reasons, score));
    }

    /// <summary>Evaluates a batch and keeps only the firing candidates, in input order.</summary>
    public static IReadOnlyList<DealAlertDecision> EvaluateAll(
        IReadOnlyList<DealAlertCandidate> candidates,
        DealAlertContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var triggered = new List<DealAlertDecision>();
        foreach (var candidate in candidates)
        {
            var decision = Evaluate(candidate, context);
            if (decision.Triggered)
            {
                triggered.Add(decision);
            }
        }

        return triggered;
    }

    /// <summary>
    /// Freshness, region and comparability gate. A stale snapshot, a row of another region, a zero/negative/
    /// absurd price, an unconverted amount or Epic's deferred free rows never reach the OR rules.
    ///
    /// The region gate is what keeps the notifier and the discover catalog on the same evidence (spec S2/S3):
    /// the catalog is MX-only, and it is MX-only by an exact SQL comparison, so an offer another region's store
    /// quoted — even one an FX pass converted under 50 MXN, even one at a verified provider low, even one that
    /// would score above 7 — is not a deal this person can act on. It is refused here rather than after the
    /// rules fire, so no reason, no score and no message is ever built for it.
    /// </summary>
    public static bool IsComparable(DealAlertCandidate candidate, DealAlertContext context)
    {
        // Ordinal and untrimmed on purpose: the catalog's SQL is `region = 'mx'`, and a policy that folded case
        // or whitespace here would push rows the catalog can never show.
        if (!string.Equals(candidate.Region, Region, StringComparison.Ordinal))
        {
            return false;
        }

        if (candidate.MxnCurrentPriceMinor <= 0 || candidate.MxnCurrentPriceMinor > MaxPriceMinor)
        {
            return false;
        }

        // An amount the FX pass could not convert is not an MXN price.
        if (string.Equals(candidate.PricingType, UnconvertedPricing, StringComparison.Ordinal))
        {
            return false;
        }

        // Epic's free rows are the deferred, questionable path (spec S5): excluded on purpose.
        if (string.Equals(candidate.Source, EpicSource, StringComparison.Ordinal) &&
            (candidate.MxnCurrentPriceMinor <= 0 || candidate.OriginalCurrentPriceMinor is null or <= 0))
        {
            return false;
        }

        return IsFresh(candidate.ObservedAt, context.NowUtc, context.MaxSnapshotAge);
    }

    /// <summary>Fresh means observed in the past, no older than the window. A future stamp is corrupt.</summary>
    public static bool IsFresh(DateTime observedAtUtc, DateTime nowUtc, TimeSpan maxSnapshotAge)
    {
        var age = nowUtc - observedAtUtc;
        return age >= TimeSpan.Zero && age <= maxSnapshotAge;
    }

    /// <summary>
    /// Rule 3. The low must come from the offer's own provider row (same source by construction) and be
    /// quoted in the same currency the offer itself is priced in, and the current original price must be
    /// at or below it. A Steam direct row is refused outright: our own observations created it, so it is
    /// not historical-low evidence.
    /// </summary>
    public static bool HasVerifiedHistoryLow(DealAlertCandidate candidate)
    {
        if (string.Equals(candidate.Source, SteamSource, StringComparison.Ordinal))
        {
            return false;
        }

        return candidate.HistoryLowAllMinor is > 0 &&
            !string.IsNullOrWhiteSpace(candidate.HistoryLowCurrency) &&
            string.Equals(candidate.HistoryLowCurrency, candidate.OriginalCurrency, StringComparison.Ordinal) &&
            candidate.OriginalCurrentPriceMinor is > 0 &&
            candidate.OriginalCurrentPriceMinor <= candidate.HistoryLowAllMinor;
    }

    /// <summary>
    /// Dedup identity: policy version + game + source + offer + price. The unique index on
    /// <c>dedup_key</c> turns a re-evaluation of the same live deal into a no-op, so a daily job cannot
    /// repeat the same offer endlessly; a changed price is a new key and does alert again.
    /// </summary>
    public static string DedupKey(DealAlertCandidate candidate)
    {
        var game = candidate.GameId is { } gameId
            ? $"g{gameId}"
            : candidate.SteamGameId is { } steamGameId
                ? $"s{steamGameId}"
                : "none";
        return $"v{TriggerVersion}|{game}|{candidate.Source}|{candidate.OfferKey}|{candidate.MxnCurrentPriceMinor}";
    }

    /// <summary>
    /// Exact port of <c>discountPercent</c> from the wishlist metrics module. Returns null when the base
    /// is missing, non-positive or not MXN, or when there is no best price.
    /// </summary>
    public static double? DiscountPercent(int? basePriceMinor, string? baseCurrency, int? bestMinor)
    {
        if (!string.Equals(baseCurrency, MxnCurrency, StringComparison.Ordinal))
        {
            return null;
        }

        if (basePriceMinor is null || basePriceMinor <= 0)
        {
            return null;
        }

        if (bestMinor is null)
        {
            return null;
        }

        return Round1((basePriceMinor.Value - bestMinor.Value) / (double)basePriceMinor.Value * 100d);
    }

    /// <summary>
    /// Exact port of <c>dealScore</c> from the wishlist metrics module: 7 points for the discount scale
    /// against the viable threshold plus 3 points for proximity to the history low.
    /// </summary>
    public static double? DealScore(
        int? basePriceMinor,
        string? baseCurrency,
        int? bestMinor,
        int? historyLowMinor,
        string? historyLowCurrency,
        int minViableDiscountPercent)
    {
        var discount = DiscountPercent(basePriceMinor, baseCurrency, bestMinor);
        if (discount is null)
        {
            return null;
        }

        var threshold = (double)Clamp(minViableDiscountPercent, 0, 95);
        double discountFactor;
        if (threshold >= ScoreCeiling)
        {
            discountFactor = discount.Value >= threshold ? 1d : 0d;
        }
        else
        {
            discountFactor = Clamp((discount.Value - threshold) / (ScoreCeiling - threshold), 0d, 1d);
        }

        var lowFactor = 0d;
        if (string.Equals(historyLowCurrency, MxnCurrency, StringComparison.Ordinal) &&
            historyLowMinor is > 0 &&
            bestMinor is not null)
        {
            lowFactor = Clamp(historyLowMinor.Value / (double)bestMinor.Value, 0d, 1d);
        }

        return Round1(7d * discountFactor + 3d * lowFactor);
    }

    private static DealAlertMessage BuildMessage(
        DealAlertCandidate candidate,
        IReadOnlyList<string> reasons,
        double? score)
    {
        var price = FormatMinor(candidate.MxnCurrentPriceMinor);
        var title = Truncate($"{candidate.GameName} — MX${price}");
        var parts = new List<string>(4) { $"{candidate.ShopName}: MX${price}" };

        if (reasons.Contains(DealAlertReasons.HistoryLow))
        {
            parts.Add("mínimo histórico verificado");
        }

        if (reasons.Contains(DealAlertReasons.Price))
        {
            parts.Add("< MX$50");
        }

        if (reasons.Contains(DealAlertReasons.Score) && score is { } value)
        {
            parts.Add($"nota {value.ToString("0.0", CultureInfo.InvariantCulture)}/10");
        }

        return new DealAlertMessage(title, TruncateBody(string.Join(" · ", parts)), candidate.DealUrl);
    }

    private static string FormatMinor(int minor) =>
        (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];

    private static string TruncateBody(string value) => value.Length <= 1024 ? value : value[..1024];

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

    /// <summary>Same rounding as JavaScript <c>Math.round</c>: half rounds toward positive infinity.</summary>
    private static double Round1(double value) => Math.Floor(value * 10d + 0.5d) / 10d;
}
