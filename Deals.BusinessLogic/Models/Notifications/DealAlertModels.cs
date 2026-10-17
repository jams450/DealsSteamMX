namespace Deals.BusinessLogic.Models.Notifications;

/// <summary>
/// One fresh persisted comparable GAME price snapshot, flattened for the alert policy.
///
/// It is a single <c>game_offers</c> row plus the two pieces of game context the wishlist formula needs:
/// the Steam list price used as the discount base, and the game-level provider history low in MXN.
///
/// Deliberate omissions:
/// - <c>external_bundles</c> / tier shares never reach this type. A bundle is not a per-game offer.
/// - <c>steam_games.lowest_price_minor</c> never does either. That value is created by our own
///   observations (on the first observation current == lowest), so it is not history-low evidence.
///   Only <see cref="HistoryLowAllMinor"/>, reported by the offer's own provider, can be.
/// </summary>
public sealed record DealAlertCandidate(
    long? GameId,
    int? SteamGameId,
    string GameName,
    string Source,
    string OfferKey,
    string ShopName,
    string Classification,
    string PricingType,
    string Region,
    string OriginalCurrency,
    int? OriginalCurrentPriceMinor,
    int MxnCurrentPriceMinor,
    /// <summary>Provider-reported history low of this exact offer row. Null when the provider reported none.</summary>
    int? HistoryLowAllMinor,
    /// <summary>Currency of <see cref="HistoryLowAllMinor"/>; must match <see cref="OriginalCurrency"/> to be evidence.</summary>
    string? HistoryLowCurrency,
    DateTime ObservedAt,
    /// <summary>Steam undiscounted list price in <see cref="BaseCurrency"/>. Null for games Steam does not sell.</summary>
    int? BasePriceMinor,
    string? BaseCurrency,
    /// <summary>Game-level provider history low already restricted to MXN, the wishlist score input.</summary>
    int? MxnHistoryLowMinor,
    string? MxnHistoryLowCurrency,
    string? DealUrl);

/// <summary>Everything the policy needs that is not part of the offer itself.</summary>
public sealed record DealAlertContext(
    DateTime NowUtc,
    TimeSpan MaxSnapshotAge,
    int MinViableDiscountPercent);

/// <summary>Reasons a candidate is considered an alert. Stable strings: they are persisted.</summary>
public static class DealAlertReasons
{
    public const string Price = "price";
    public const string Score = "score";
    public const string HistoryLow = "history_low";
}

/// <summary>Ready-to-send text. Built once by the policy, never rebuilt by the dispatcher.</summary>
public sealed record DealAlertMessage(string Title, string Body, string? ClickUrl);

/// <summary>
/// Policy outcome for one candidate. <see cref="Triggered"/> is false when the candidate is excluded
/// (invalid, stale, unconverted, free Epic) or clears none of the three OR rules.
/// </summary>
public sealed record DealAlertDecision(
    bool Triggered,
    DealAlertCandidate Candidate,
    IReadOnlyList<string> Reasons,
    double? Score,
    string DedupKey,
    DealAlertMessage? Message);
