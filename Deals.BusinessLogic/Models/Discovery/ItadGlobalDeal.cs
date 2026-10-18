namespace Deals.BusinessLogic.Models.Discovery;

/// <summary>
/// One individual deal as returned by the ITAD global deals catalog (<c>deals/v2</c>). This is a
/// provider-shaped, sanitized record: the raw payload never travels past the client.
///
/// Unlike the wishlist summary, this feed is not driven by a queried game set: it is a global
/// discovery list. Every field is read from the provider response; nothing here is synthesized.
/// <see cref="Currency"/> and <see cref="CurrentPriceMinor"/> are the provider's own price and
/// currency (MX may be quoted in USD); the MXN projection is derived later by the writer and is
/// explicitly labelled in the catalog.
/// </summary>
public sealed record ItadGlobalDeal(
    string DealKey,
    string ItadGameId,
    string GameTitle,
    string? GameSlug,
    string? GameType,
    bool GameMature,
    string ShopId,
    string ShopName,
    bool IsOfficial,
    string Currency,
    int CurrentPriceMinor,
    int? RegularPriceMinor,
    int? DiscountPercent,
    int? HistoryLowMinor,
    string? HistoryLowCurrency,
    string? DealUrl,
    DateTime? ExpiresAt);

/// <summary>
/// One page of the global deals catalog plus its pagination cursor. <see cref="HasMore"/> comes from
/// the provider's own flag, not from a short-page heuristic: <c>deals/v2</c> reports it explicitly.
/// <see cref="NextOffset"/> is the provider's cursor when it reports a usable one, otherwise the
/// client's computed <c>offset + observed items</c>. When <see cref="HasMore"/> is true and
/// <see cref="NextOffset"/> does not advance, the sweep treats the cursor as a loop and preserves the
/// previous snapshot instead of certifying a partial catalog.
/// </summary>
public sealed record GlobalDealPage(
    IReadOnlyList<ItadGlobalDeal> Deals,
    bool HasMore,
    int? NextOffset);

/// <summary>
/// Dedicated source literal for the global deals snapshot. It is never <c>itad</c> or
/// <c>itad_global</c> (the bundle sweep): the per-game orphan purge is scoped to <c>itad</c>, so a
/// distinct literal is what keeps this feed out of any other provider's cleanup.
/// </summary>
public static class GlobalDealSources
{
    public const string GlobalDeals = "itad_global_deals";
}
