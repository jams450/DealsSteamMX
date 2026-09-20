namespace Deals.BusinessLogic.Models.Stores;

/// <summary>
/// One store offer already priced in the store's own regional currency. Provider-neutral: it carries no
/// ITAD-style shop classification and no FX, because a direct store call answers in the region's currency
/// (Epic MX answers MXN). The caller decides how the offer is persisted.
///
/// <see cref="ExternalId"/> is the id the store itself uses in its public URLs and the one ITAD accepts for
/// that shop, so it is what belongs in <c>game_external_ids</c>. <see cref="ShopId"/> is the store-side
/// artifact id (Epic: the catalog offer id) and lands in <c>game_offers.shop_id</c>.
/// </summary>
public sealed record StoreOffer(
    string Source,
    string ExternalId,
    string ShopId,
    string ShopName,
    string Title,
    int RegularPriceMinor,
    int CurrentPriceMinor,
    string Currency,
    int? DiscountPercent,
    string DealUrl);
