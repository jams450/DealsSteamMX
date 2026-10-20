using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Discovery;
using Deals.BusinessLogic.Models.Itad;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using System.Text.Json;

// Deterministic checks for the equal-share allocation rule of the bundle catalog (PLAN_BUNDLES.md).
// The rule is a pure function so it can be exercised without a database: the whole point is that a
// tier with an unknown price, a BYO tier, an addon or an incomplete item list must never publish an
// invented per-game figure.
var failures = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        failures.Add(name);
    }
}

static SteamGameBundleTier Tier(
    int? priceMinor,
    string? currency,
    bool addon,
    bool itemsComplete,
    params (string Title, string? Type, int? PriceMinor)[] items) =>
    new(priceMinor, currency, addon, itemsComplete,
        items.Select(item => new SteamGameBundleTierGame(item.Title, item.Type, item.PriceMinor, "USD")).ToList());

static SteamGameBundleTier MatchTier(params (string ItadId, string Title)[] items) =>
    new(1000, "USD", false, true,
        items.Select(item => new SteamGameBundleTierGame(item.Title, "game", 100, "USD", item.ItadId)).ToList());

// --- Allocate -------------------------------------------------------------

var ok = BundleShare.Allocate(Tier(1000, "USD", false, true, ("A", "game", 500), ("B", "game", 700)));
Check(ok.AllocationStatus == BundleShare.OkStatus, "a complete tier with a price and two games is allocatable");
Check(ok.EligibleGameCount == 2, "both plain games count as eligible");
Check(ok.ItemCount == 2, "the item count reports every listed item");
Check(ok.AllocatedPerGameMinor == 500, "1000 minor units split by 2 is 500");
Check(ok.RequiresWholeTierPurchase, "an allocated share always requires buying the whole tier");
Check(ok.PriceMinor == 1000 && ok.Currency == "USD", "the tier's own price and currency are preserved");

var roundsUp = BundleShare.Allocate(Tier(1001, "USD", false, true, ("A", "game", 1), ("B", "game", 1), ("C", "game", 1)));
Check(roundsUp.AllocatedPerGameMinor == 334, "an inexact split rounds to the nearest minor unit");

var nonGames = BundleShare.Allocate(Tier(900, "USD", false, true, ("A", "game", 1), ("B", "dlc", 1), ("C", "package", 1)));
Check(nonGames.EligibleGameCount == 1, "DLC and packages are not eligible games");
Check(nonGames.ItemCount == 3, "non-game items still count as items");
Check(nonGames.AllocatedPerGameMinor == 900, "the whole tier price goes to the only eligible game");

var free = BundleShare.Allocate(Tier(0, "USD", false, true, ("A", "game", 1)));
Check(free.AllocationStatus == BundleShare.OkStatus, "zero is a published price, not an unknown one");
Check(free.AllocatedPerGameMinor == 0, "a free tier divides to a real zero");

var addon = BundleShare.Allocate(Tier(1000, "USD", true, true, ("A", "game", 1)));
Check(addon.AllocationStatus == BundleShare.AddonStatus && addon.AllocatedPerGameMinor is null, "an addon tier publishes no share");

var incomplete = BundleShare.Allocate(Tier(1000, "USD", false, false, ("A", "game", 1)));
Check(incomplete.AllocationStatus == BundleShare.ItemsIncompleteStatus && incomplete.AllocatedPerGameMinor is null,
    "an incomplete item list publishes no share");

var noPrice = BundleShare.Allocate(Tier(null, "USD", false, true, ("A", "game", 1)));
Check(noPrice.AllocationStatus == BundleShare.NoTierPriceStatus && noPrice.AllocatedPerGameMinor is null,
    "an unknown or build-your-own tier price publishes no share");

var noCurrency = BundleShare.Allocate(Tier(1000, " ", false, true, ("A", "game", 1)));
Check(noCurrency.AllocationStatus == BundleShare.CurrencyUnknownStatus && noCurrency.AllocatedPerGameMinor is null,
    "a price without a currency publishes no share");

var noEligible = BundleShare.Allocate(Tier(1000, "USD", false, true, ("A", "dlc", 1)));
Check(noEligible.AllocationStatus == BundleShare.NoEligibleItemsStatus && noEligible.AllocatedPerGameMinor is null,
    "a tier with no eligible game publishes no share");

// --- compare against a standalone price -----------------------------------

var comparable = BundleShare.Allocate(Tier(1000, "USD", false, true, ("A", "game", 1), ("B", "game", 1)));
Check(BundleShare.IsShareCheaperThanStandalone(comparable, 600, "USD") == true, "600 standalone beats a 500 share");
Check(BundleShare.IsShareCheaperThanStandalone(comparable, 500, "USD") == false, "an equal price is not cheaper");
Check(BundleShare.IsShareCheaperThanStandalone(comparable, 400, "USD") == false, "400 standalone is cheaper than the share");
Check(BundleShare.IsShareCheaperThanStandalone(comparable, 600, "MXN") is null, "a different currency cannot be compared");
Check(BundleShare.IsShareCheaperThanStandalone(comparable, null, "USD") is null, "an unknown standalone price cannot be compared");

// Exactness: 1001 split by 3 rounds to 334, but the honest comparison must use the real share
// (333.67), so a 334 standalone price is still beaten. Using the rounded figure would hide that.
var exact = BundleShare.Allocate(Tier(1001, "USD", false, true, ("A", "game", 1), ("B", "game", 1), ("C", "game", 1)));
Check(BundleShare.IsShareCheaperThanStandalone(exact, 334, "USD") == true,
    "the comparison uses the exact division, never the rounded display figure");

Check(BundleShare.IsShareCheaperThanStandalone(addon, 100, "USD") is null, "an unallocatable tier cannot be compared");
Check(BundleShare.IsShareCheaperThanStandalone(noPrice, 100, "USD") is null, "a tier without a price cannot be compared");

// --- ITAD item UUID preserved through the persisted tier mapping ------------------------------
// The membership of a game inside a tier is compared by this UUID (never by title), so it must
// survive the sanitized mapping and the JSON that is stored in external_bundles.tiers_json.

var itadTier = new ItadBundleTier(1000, "USD", false, true, new[]
{
    new ItadBundleItem("itad-a", "A", "game", 500, "USD"),
    new ItadBundleItem("itad-b", "B", "game", 700, "USD")
});
var mapped = BundleTierMapper.Map(new[] { itadTier }, 20, 50);
Check(mapped.Count == 1, "one itad tier maps to one persisted tier");
Check(mapped[0].Games[0].ItadGameId == "itad-a" && mapped[0].Games[1].ItadGameId == "itad-b",
    "the ITAD item UUID is preserved through the tier mapping");

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var persistedJson = JsonSerializer.Serialize(mapped, jsonOptions);
var restored = JsonSerializer.Deserialize<List<SteamGameBundleTier>>(persistedJson, jsonOptions)!;
Check(persistedJson.Contains("\"itadGameId\":\"itad-a\"", StringComparison.Ordinal),
    "the persisted JSON carries the item UUID");
Check(restored[0].Games[0].ItadGameId == "itad-a" && restored[0].Games[1].ItadGameId == "itad-b",
    "the item UUID round-trips through the persisted JSON");

var truncated = BundleTierMapper.Map(new[] { itadTier }, 20, 1);
Check(truncated[0].Games.Count == 1 && !truncated[0].ItemsComplete,
    "a persistence-truncated tier is marked incomplete");

// --- Catalog dedup / pagination / wishlist matching -------------------------------------------
// The catalog reads two sources (itad and itad_global) and must collapse a bundle seen in both,
// paginate honestly and highlight the current user's wishlist without hiding non-wishlist bundles.
// Every rule below is a pure function so it is exercised without a database.

static BundleCatalogSnapshot Snap(string source, string key, DateTime observedAt, DateTime? expiresAt = null, string? title = null) =>
    new(source, key, title ?? key, expiresAt, observedAt);

var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

var fresher = BundleCatalogOrder.Deduplicate(new[]
{
    Snap("itad", "b1", t0),
    Snap("itad_global", "b1", t0.AddHours(1))
});
Check(fresher.Count == 1 && fresher[0].Source == "itad_global",
    "a bundle seen in both sources keeps the fresher snapshot");

var tied = BundleCatalogOrder.Deduplicate(new[]
{
    Snap("itad_global", "b1", t0),
    Snap("itad", "b1", t0)
});
Check(tied.Count == 1 && tied[0].Source == "itad", "an equal timestamp prefers itad");

var ordered = BundleCatalogOrder.Deduplicate(new[]
{
    Snap("itad", "b2", t0, null, "Sin fecha"),
    Snap("itad", "b1", t0, t0.AddDays(1), "Con fecha"),
    Snap("itad_global", "b3", t0, t0.AddDays(2), "Otro")
});
Check(ordered.Count == 3, "distinct bundle keys are all retained");
Check(ordered[0].BundleKey == "b1" && ordered[2].BundleKey == "b2", "soonest expiry first and no-date last");

var many = Enumerable.Range(0, 7)
    .Select(i => Snap("itad", $"k{i}", t0, t0.AddDays(1), $"T{i:00}"))
    .ToList();
var page1 = BundleCatalogPager.Paginate(many, 1, 3);
Check(page1.Total == 7 && page1.Items.Count == 3 && page1.HasMore,
    "page 1 of 7 with size 3 has three items and more");
Check(page1.Items.Count == 3 && page1.Total == 7 && many.Count == 7,
    "pagination never filters items by wishlist");
var page3 = BundleCatalogPager.Paginate(many, 3, 3);
Check(page3.Items.Count == 1 && !page3.HasMore, "the last page reports the remainder and no more");
var pagePast = BundleCatalogPager.Paginate(many, 9, 3);
Check(pagePast.Items.Count == 0 && !pagePast.HasMore && pagePast.Total == 7,
    "a page past the end is empty but keeps the honest total");
Check(page1.Total == 7 && page1.Total != BundleCatalogPager.MaxPageSize,
    "the honest total is never the fixed page-size bound");
Check(BundleCatalogPager.NormalizePageSize(1000) == BundleCatalogPager.MaxPageSize,
    "a huge page size is clamped to the contract bound");
Check(BundleCatalogPager.NormalizePage(null) == 1 && BundleCatalogPager.NormalizePage(0) == 1,
    "a missing or invalid page defaults to 1");
Check(BundleCatalogPager.NormalizePageSize(null) == BundleCatalogPager.DefaultPageSize,
    "a missing page size defaults to the contract default");

var library = new[]
{
    new BundleWishlistLibraryRow(1, "steam", "wished", "100"),
    new BundleWishlistLibraryRow(1, "steam", "owned", "200"),
    new BundleWishlistLibraryRow(2, "steam", "wished", "300"),
    new BundleWishlistLibraryRow(1, "epic", "wished", "999")
};
var gameRows = new[]
{
    new BundleWishlistGameRow(100, "Wished A", "itad-a", "mx"),
    new BundleWishlistGameRow(200, "Owned B", "itad-b", "mx"),
    new BundleWishlistGameRow(300, "Other C", "itad-c", "mx")
};
var resolved = BundleWishlist.Resolve(1, library, gameRows);
Check(resolved.Count == 1 && resolved[0].AppId == 100,
    "only the current user's wished steam rows resolve, never another user's or another store's");

var nonMx = BundleWishlist.Resolve(1, library, new[] { new BundleWishlistGameRow(100, "Wished A", "itad-a", "us") });
Check(nonMx.Count == 0, "a wished appid whose catalog row is not MX is not a match identity");

var wish = new[] { new BundleWishlistEntry(100, "Wished A", "itad-a") };
var exactTiers = new[] { MatchTier(("itad-a", "Titulo distinto al de la wishlist")) };
Check(BundleWishlist.Match(wish, exactTiers, []).Count == 1,
    "a tier item UUID matches even when the title differs");
var sameTitleWrongId = new[] { MatchTier(("itad-zzz", "Wished A")) };
Check(BundleWishlist.Match(wish, sameTitleWrongId, []).Count == 0,
    "an equal title with a different UUID is never a match");
Check(BundleWishlist.Match(wish, [], [new BundleCatalogMember(100, "Wished A", null)]).Count == 1,
    "a proven linked appid is a valid fallback match");
Check(BundleWishlist.Match([new BundleWishlistEntry(100, "Wished A", null)], [], new[]
    {
        new BundleCatalogMember(100, "Wished A", null),
        new BundleCatalogMember(101, "Otra", null)
    }).Single().AppId == 100,
    "a wishlist entry without an ITAD id still matches through its proven appid");
Check(BundleWishlist.Match(wish, exactTiers, []).Single().Name == "Wished A",
    "the match reports the wishlist game name, not the provider title");

// --- gg.deals selection ratio + source isolation (T6) ------------------------------------------
// gg.deals publishes a "choose N of M" count instead of listing only the included games. The count is
// the honest denominator when it is satisfiable; a count outside the listed set is incomplete. ITAD
// tiers carry no count and keep their existing eligible-game denominator (defaults preserved).

var byoTier = new SteamGameBundleTier(
    1000, "USD", false, true,
    Enumerable.Range(0, 10)
        .Select(index => new SteamGameBundleTierGame($"G{index}", "game", null, null, null, $"https://gg.deals/game/{index}/"))
        .ToList(),
    SelectionCount: 2);
var byo = BundleShare.Allocate(byoTier);
Check(byo.SelectionCount == 2, "the choose-N rule survives allocation");
Check(byo.AllocatedPerGameMinor == 500, "the price divides by the selection count, not the listed game count");
Check(byo.EligibleGameCount == 10, "the eligible count still reports every listed game");
Check(byo.AllocationStatus == BundleShare.OkStatus, "a satisfiable choose-N tier is allocatable");

var itadDefault = BundleShare.Allocate(Tier(1000, "USD", false, true, ("A", "game", 1), ("B", "game", 1)));
Check(itadDefault.SelectionCount is null, "an ITAD tier keeps a null selection count");
Check(itadDefault.AllocatedPerGameMinor == 500, "an ITAD tier still divides by its eligible games");

var unsatisfiable = BundleShare.Allocate(new SteamGameBundleTier(
    1000, "USD", false, true,
    new[] { new SteamGameBundleTierGame("A", "game", null, null, null, null) },
    SelectionCount: 5));
Check(unsatisfiable.AllocationStatus == BundleShare.ItemsIncompleteStatus,
    "a choose-N larger than the listed games is incomplete and earns no share");
Check(unsatisfiable.AllocatedPerGameMinor is null, "an unsatisfiable tier never publishes a figure");

var flatTier = new SteamGameBundleTier(500, "USD", false, true,
    new[] { new SteamGameBundleTierGame("A", "game", null, null, null, "https://gg.deals/game/a/") },
    SelectionCount: null);
Check(flatTier.Games[0].Url == "https://gg.deals/game/a/", "the gg.deals item URL survives on the tier item");
Check(flatTier.SelectionCount is null, "a tier with no choose-N rule defaults to null");

var noCrossSource = BundleCatalogOrder.Deduplicate(new[]
{
    Snap("itad", "gg:abc", t0),
    Snap("ggdeals", "gg:abc", t0.AddHours(1))
});
Check(noCrossSource.Count == 2, "a gg.deals key never deduplicates against an ITAD bundle even when the key text matches");
Check(noCrossSource.Any(snapshot => snapshot.Source == BundleCatalogOrder.GgDealsSource),
    "the gg.deals snapshot survives the cross-source dedup");

// --- Dual currency money block (S1, shared tier contract L4) -----------------------------------
// The catalog keeps the tier's own price and currency and adds a USD/MXN reading built only from the
// persisted FX rows: forward USD/MXN for a USD tier, explicit MXN/USD for an MXN tier. A missing pair
// keeps the native figure and leaves the other currency null (never 0), and the inverse pair is never
// synthesized from the forward one. Unknown prices stay unknown.

var usdMxnDate = new DateOnly(2026, 10, 4);
var mxnUsdDate = new DateOnly(2026, 10, 3);
var usdToMxn = new GlobalDealFxRate(17.5m, usdMxnDate, "banxico");
var mxnToUsd = new GlobalDealFxRate(0.055m, mxnUsdDate, "frankfurter");
var fx = new BundleFxRates(usdToMxn, mxnToUsd);
var noFx = BundleFxRates.None;

var usdMoney = BundleCurrencyProjection.ForAmount(1499, "USD", fx);
Check(usdMoney.UsdMinor == 1499, "a native USD amount keeps its own figure");
Check(usdMoney.MxnMinor == 26233, "the MXN reading is the converted native amount");
Check(usdMoney.FxRate == 17.5m && usdMoney.FxRateDate == usdMxnDate && usdMoney.FxSource == "banxico",
    "a USD tier reports the exact forward pair it used");
Check(usdMoney.PricingType == GlobalDealPricingTypes.FxEstimate, "a converted reading is an fx estimate");
Check(BundleCurrencyProjection.ForAmount(1499, " usd ", fx).UsdMinor == 1499,
    "the currency code is matched case-insensitively and trimmed");

var usdWithoutPair = BundleCurrencyProjection.ForAmount(1499, "USD", noFx);
Check(usdWithoutPair.UsdMinor == 1499 && usdWithoutPair.MxnMinor is null,
    "a USD amount without a persisted pair keeps USD and claims no MXN");
Check(usdWithoutPair.FxRate is null && usdWithoutPair.FxRateDate is null && usdWithoutPair.FxSource is null,
    "no conversion means no FX metadata");
Check(usdWithoutPair.PricingType == GlobalDealPricingTypes.Unconverted,
    "a USD amount without a pair is unconverted, not regional");

var usdOnlyInverse = BundleCurrencyProjection.ForAmount(1499, "USD", new BundleFxRates(null, mxnToUsd));
Check(usdOnlyInverse.MxnMinor is null,
    "the MXN/USD pair is never inverted to price a USD tier");

var mxnMoney = BundleCurrencyProjection.ForAmount(29900, "MXN", fx);
Check(mxnMoney.MxnMinor == 29900, "a native MXN amount keeps its own figure");
Check(mxnMoney.UsdMinor == 1645, "the USD reading uses the explicit MXN/USD pair");
Check(mxnMoney.FxRate == 0.055m && mxnMoney.FxRateDate == mxnUsdDate && mxnMoney.FxSource == "frankfurter",
    "an MXN tier reports the exact MXN/USD pair it used");
Check(mxnMoney.PricingType == GlobalDealPricingTypes.FxEstimate, "a converted MXN reading is an fx estimate");

var mxnWithoutPair = BundleCurrencyProjection.ForAmount(29900, "MXN", new BundleFxRates(usdToMxn, null));
Check(mxnWithoutPair.MxnMinor == 29900 && mxnWithoutPair.UsdMinor is null,
    "an MXN amount without an explicit MXN/USD pair keeps MXN and claims no USD");
Check(mxnWithoutPair.FxRate is null && mxnWithoutPair.PricingType == GlobalDealPricingTypes.Regional,
    "an MXN amount without a pair is regional, never a fabricated estimate");

var unsupported = BundleCurrencyProjection.ForAmount(5000, "EUR", fx);
Check(unsupported.UsdMinor is null && unsupported.MxnMinor is null,
    "an unsupported currency never claims a USD or MXN figure");
Check(unsupported.PricingType == GlobalDealPricingTypes.Unconverted && unsupported.FxRate is null,
    "an unsupported currency is unconverted and carries no rate");

var unknownUsd = BundleCurrencyProjection.ForAmount(null, "USD", fx);
Check(unknownUsd.UsdMinor is null && unknownUsd.MxnMinor is null,
    "an unknown price stays null in both currencies instead of becoming 0");
Check(unknownUsd.PricingType == GlobalDealPricingTypes.Unconverted, "an unknown price claims nothing");
var unknownMxn = BundleCurrencyProjection.ForAmount(null, "MXN", fx);
Check(unknownMxn.MxnMinor is null && unknownMxn.UsdMinor is null,
    "an unknown MXN price stays null in both currencies");

var freeUsd = BundleCurrencyProjection.ForAmount(0, "USD", fx);
Check(freeUsd.UsdMinor == 0 && freeUsd.MxnMinor == 0,
    "a published zero stays a real zero in both currencies");
Check(freeUsd.FxRate == 17.5m && freeUsd.PricingType == GlobalDealPricingTypes.FxEstimate,
    "the justified zero carries the rate that produced it");
var freeMxn = BundleCurrencyProjection.ForAmount(0, "MXN", fx);
Check(freeMxn.MxnMinor == 0 && freeMxn.UsdMinor == 0, "a free MXN tier is zero in both currencies");
var freeWithoutPair = BundleCurrencyProjection.ForAmount(0, "USD", noFx);
Check(freeWithoutPair.UsdMinor == 0 && freeWithoutPair.MxnMinor is null,
    "a zero is not invented for a currency that has no rate");

var zeroRate = BundleCurrencyProjection.ForAmount(1000, "USD",
    new BundleFxRates(new GlobalDealFxRate(0m, usdMxnDate, "banxico"), null));
Check(zeroRate.MxnMinor is null && zeroRate.FxRate is null,
    "a non-positive persisted rate converts nothing");
Check(zeroRate.UsdMinor == 1000 && zeroRate.PricingType == GlobalDealPricingTypes.Unconverted,
    "a tier whose rate is unusable keeps only its native figure");
var negativeRate = BundleCurrencyProjection.ForAmount(1000, "USD",
    new BundleFxRates(new GlobalDealFxRate(-3m, usdMxnDate, "banxico"), null));
Check(negativeRate.MxnMinor is null, "a negative persisted rate converts nothing");

var overflow = BundleCurrencyProjection.ForAmount(int.MaxValue, "USD",
    new BundleFxRates(new GlobalDealFxRate(2m, usdMxnDate, "banxico"), null));
Check(overflow.MxnMinor is null && overflow.FxRate is null,
    "an overflowing conversion emits no MXN figure and no rate");
Check(overflow.UsdMinor == int.MaxValue && overflow.PricingType == GlobalDealPricingTypes.Unconverted,
    "an overflowing conversion keeps the native figure intact");

// The per-tier projection wraps the untouched equal share and only allocates when the share is honest.
var roundedTotal = BundleShare.Allocate(Tier(1001, "USD", false, true, ("A", "game", 1), ("B", "game", 1), ("C", "game", 1)));
var doubled = new BundleFxRates(new GlobalDealFxRate(2m, usdMxnDate, "banxico"), null);
var projectedTier = BundleCurrencyProjection.Apply(roundedTotal, doubled);
Check(projectedTier.PriceMoney is not null && projectedTier.PriceMoney.UsdMinor == 1001,
    "the tier total keeps its native USD figure");
Check(projectedTier.PriceMoney!.MxnMinor == 2002, "the tier total is converted once");
Check(projectedTier.AllocatedPerGameMoney is not null, "an allocatable tier emits an allocated money block");
Check(projectedTier.AllocatedPerGameMoney!.UsdMinor == roundedTotal.AllocatedPerGameMinor,
    "the allocated USD figure is the untouched equal share");
Check(projectedTier.AllocatedPerGameMoney!.MxnMinor == 667,
    "the converted share divides the converted total, not the converted share");
Check(projectedTier.AllocatedPerGameMoney!.MxnMinor != 668,
    "converting the rounded share first would report a different figure");

// PRESERVE: the projection is additive, the existing tier contract keeps every value it had.
Check(projectedTier.PriceMinor == roundedTotal.PriceMinor && projectedTier.Currency == roundedTotal.Currency,
    "the projection preserves the tier's own price and currency");
Check(projectedTier.AllocatedPerGameMinor == roundedTotal.AllocatedPerGameMinor &&
    projectedTier.AllocationStatus == roundedTotal.AllocationStatus &&
    projectedTier.EligibleGameCount == roundedTotal.EligibleGameCount &&
    projectedTier.ItemCount == roundedTotal.ItemCount &&
    projectedTier.SelectionCount == roundedTotal.SelectionCount &&
    projectedTier.Addon == roundedTotal.Addon &&
    projectedTier.ItemsComplete == roundedTotal.ItemsComplete &&
    projectedTier.RequiresWholeTierPurchase == roundedTotal.RequiresWholeTierPurchase,
    "every existing tier field survives the projection unchanged");

var projectedMxnShare = BundleCurrencyProjection.Apply(
    BundleShare.Allocate(Tier(900, "MXN", false, true, ("A", "game", 1), ("B", "game", 1), ("C", "game", 1))),
    new BundleFxRates(null, new GlobalDealFxRate(0.05m, mxnUsdDate, "frankfurter")));
Check(projectedMxnShare.PriceMoney!.MxnMinor == 900 && projectedMxnShare.PriceMoney.UsdMinor == 45,
    "an MXN tier total is read in both currencies");
Check(projectedMxnShare.AllocatedPerGameMoney!.MxnMinor == 300,
    "the MXN share keeps the native equal share");
Check(projectedMxnShare.AllocatedPerGameMoney!.UsdMinor == 15,
    "the converted MXN share divides the converted total by the same count");

var addonMoney = BundleCurrencyProjection.Apply(
    BundleShare.Allocate(Tier(1000, "USD", true, true, ("A", "game", 1))), fx);
Check(addonMoney.AllocatedPerGameMoney is null, "an addon tier emits no allocated money block");
var incompleteMoney = BundleCurrencyProjection.Apply(
    BundleShare.Allocate(Tier(1000, "USD", false, false, ("A", "game", 1))), fx);
Check(incompleteMoney.AllocatedPerGameMoney is null, "an incomplete tier emits no allocated money block");
var noEligibleMoney = BundleCurrencyProjection.Apply(
    BundleShare.Allocate(Tier(1000, "USD", false, true, ("A", "dlc", 1))), fx);
Check(noEligibleMoney.AllocatedPerGameMoney is null, "a tier with no eligible game emits no allocated money block");
var noPriceMoney = BundleCurrencyProjection.Apply(
    BundleShare.Allocate(Tier(null, "USD", false, true, ("A", "game", 1))), fx);
Check(noPriceMoney.AllocatedPerGameMoney is null, "a tier without a price emits no allocated money block");
Check(noPriceMoney.PriceMoney is not null && noPriceMoney.PriceMoney.UsdMinor is null &&
    noPriceMoney.PriceMoney.MxnMinor is null,
    "a tier without a price emits a money block with no figure");

var alreadyConverted = BundleCurrencyProjection.Apply(roundedTotal, noFx);
Check(alreadyConverted.AllocatedPerGameMoney!.UsdMinor == roundedTotal.AllocatedPerGameMinor &&
    alreadyConverted.AllocatedPerGameMoney!.MxnMinor is null,
    "an allocated share without a rate keeps the native figure and no MXN");

var byoShare = BundleShare.Allocate(byoTier);
var byoMoney = BundleCurrencyProjection.Apply(byoShare, doubled);
Check(byoMoney.AllocatedPerGameMoney!.UsdMinor == 500, "a choose-N tier keeps its native share");
Check(byoMoney.AllocatedPerGameMoney!.MxnMinor == 1000,
    "a choose-N tier divides its converted total by the same selection count");
Check(byoMoney.AllocatedPerGameMoney!.MxnMinor != 200,
    "a choose-N tier never divides the converted total by the listed game count");

var unsupportedShare = BundleCurrencyProjection.Apply(
    BundleShare.Allocate(Tier(1000, "EUR", false, true, ("A", "game", 1), ("B", "game", 1))), fx);Check(unsupportedShare.AllocatedPerGameMoney!.UsdMinor is null &&
    unsupportedShare.AllocatedPerGameMoney!.MxnMinor is null,
    "an unsupported tier currency never claims a USD or MXN share");
Check(unsupportedShare.AllocatedPerGameMinor == 500 && unsupportedShare.Currency == "EUR",
    "the unsupported tier keeps its native currency and share unchanged");

// A priced tier always carries a price block, even when neither currency can be claimed: the absence of
// a figure is expressed by the null amounts, never by a missing block (a missing block means "old payload").
var blankCurrency = BundleCurrencyProjection.Apply(
    BundleShare.Allocate(Tier(1000, " ", false, true, ("A", "game", 1))), fx);
Check(blankCurrency.PriceMoney is not null && blankCurrency.PriceMoney.UsdMinor is null &&
    blankCurrency.PriceMoney.MxnMinor is null && blankCurrency.PriceMoney.PricingType == GlobalDealPricingTypes.Unconverted,
    "a price with an unknown currency claims neither USD nor MXN and still emits the block");
Check(blankCurrency.AllocatedPerGameMoney is null,
    "a price with an unknown currency has no allocation to read");

// The converted share uses the same away-from-zero rounding as the native equal share: 1001 minor over
// two games is 500.5, which is 501 in both currencies (banker's rounding would report 500).
var halfShare = BundleCurrencyProjection.Apply(
    BundleShare.Allocate(Tier(1001, "USD", false, true, ("A", "game", 1), ("B", "game", 1))),
    new BundleFxRates(new GlobalDealFxRate(1m, usdMxnDate, "banxico"), null));
Check(halfShare.AllocatedPerGameMinor == 501 && halfShare.AllocatedPerGameMoney!.UsdMinor == 501,
    "the native share rounds a half away from zero");
Check(halfShare.AllocatedPerGameMoney!.MxnMinor == 501,
    "the converted share rounds a half away from zero with the same rule");

var mxnAllocationWithoutPair = BundleCurrencyProjection.Apply(    BundleShare.Allocate(Tier(900, "MXN", false, true, ("A", "game", 1), ("B", "game", 1), ("C", "game", 1))),
    new BundleFxRates(usdToMxn, null));
Check(mxnAllocationWithoutPair.AllocatedPerGameMoney!.MxnMinor == 300 &&
    mxnAllocationWithoutPair.AllocatedPerGameMoney!.UsdMinor is null,
    "an MXN share without the explicit pair keeps MXN and claims no USD");
Check(mxnAllocationWithoutPair.AllocatedPerGameMoney!.FxRate is null &&
    mxnAllocationWithoutPair.AllocatedPerGameMoney!.PricingType == GlobalDealPricingTypes.Regional,
    "an MXN share without the explicit pair reports no rate and no estimate");

// Every tier of a page is projected from the same already-loaded pairs: the projection is pure and
// takes no service, so a page of tiers cannot trigger another FX read.
var manyTiers = Enumerable.Range(0, 40)
    .Select(_ => Tier(1000, "USD", false, true, ("A", "game", 1), ("B", "game", 1)))
    .ToList();
var allProjected = BundleCurrencyProjection.ApplyAll(manyTiers, fx);
Check(allProjected.Count == manyTiers.Count, "every tier of a page is projected");
Check(allProjected.All(tier => tier.PriceMoney?.MxnMinor == 17500),
    "a page of tiers is projected from the same rate, never re-read per tier");
Check(allProjected.All(tier => tier.AllocatedPerGameMoney?.MxnMinor == 8750),
    "a page of tiers keeps the endogenous equal-share rule in both currencies");

// PRESERVE: the catalog's tier projection is still exactly BundleShare.Allocate for every field that
// existed before this change, for every allocation status (ok, addon, incomplete, no price, choose-N,
// MXN, unsupported currency).
var mixedTiers = new List<SteamGameBundleTier>
{
    Tier(1000, "USD", false, true, ("A", "game", 1), ("B", "game", 1)),
    Tier(1000, "USD", true, true, ("A", "game", 1)),
    Tier(1000, "USD", false, false, ("A", "game", 1)),
    Tier(null, "USD", false, true, ("A", "game", 1)),
    Tier(1000, " ", false, true, ("A", "game", 1)),
    Tier(1000, "EUR", false, true, ("A", "game", 1), ("B", "dlc", 1)),
    Tier(900, "MXN", false, true, ("A", "game", 1), ("B", "game", 1), ("C", "game", 1)),
    byoTier
};
var projectedMixed = BundleCurrencyProjection.ApplyAll(mixedTiers, fx);
Check(projectedMixed.Count == mixedTiers.Count, "every tier keeps its place in the page");
Check(mixedTiers.Select(BundleShare.Allocate).Zip(projectedMixed).All(pair =>
        pair.First.PriceMinor == pair.Second.PriceMinor &&
        pair.First.Currency == pair.Second.Currency &&
        pair.First.Addon == pair.Second.Addon &&
        pair.First.ItemsComplete == pair.Second.ItemsComplete &&
        pair.First.ItemCount == pair.Second.ItemCount &&
        pair.First.EligibleGameCount == pair.Second.EligibleGameCount &&
        pair.First.AllocatedPerGameMinor == pair.Second.AllocatedPerGameMinor &&
        pair.First.AllocationStatus == pair.Second.AllocationStatus &&
        pair.First.RequiresWholeTierPurchase == pair.Second.RequiresWholeTierPurchase &&
        pair.First.SelectionCount == pair.Second.SelectionCount),
    "every field the catalog read before this change is produced by the same allocation rule");

// --- FX pair loading: one read per pair, read-only ----------------------------------------------
var fxStub = new CountingFxRateService(usdToMxn, mxnToUsd);
var loaded = await BundleCatalogService.LoadFxRatesAsync(fxStub, CancellationToken.None);
Check(loaded.UsdToMxn is { Rate: 17.5m } && loaded.MxnToUsd is { Rate: 0.055m },
    "both persisted pairs are loaded into the projection");
Check(fxStub.CallsFor("USD", "MXN") == 1, "the forward pair is read once per request");
Check(fxStub.CallsFor("MXN", "USD") == 1, "the explicit inverse pair is read once per request");
Check(fxStub.TotalCalls == 2, "a catalog read performs exactly two persisted FX reads");
Check(!fxStub.FetchedAnything, "no catalog read fetches a rate from a provider");

var staleFxStub = new CountingFxRateService(
    new GlobalDealFxRate(0m, usdMxnDate, "banxico"),
    new GlobalDealFxRate(-1m, mxnUsdDate, "frankfurter"));
var unusable = await BundleCatalogService.LoadFxRatesAsync(staleFxStub, CancellationToken.None);
Check(unusable.UsdToMxn is null && unusable.MxnToUsd is null,
    "a non-positive persisted row is treated as no rate at all");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} bundle share checks failed");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"  - {failure}");
    }

    Environment.Exit(1);
}

Console.WriteLine("PASS: bundle share allocation checks");

/// <summary>
/// Counts the persisted rate reads of a catalog request. <see cref="IFxRateService.GetRateAsync"/> is the
/// provider-fetching member and throws on purpose: a read from the catalog must never reach it.
/// </summary>
internal sealed class CountingFxRateService(GlobalDealFxRate? usdToMxn, GlobalDealFxRate? mxnToUsd)
    : IFxRateService
{
    private readonly List<(string Base, string Quote)> _reads = [];

    public bool FetchedAnything { get; private set; }

    public int TotalCalls => _reads.Count;

    public int CallsFor(string baseCurrency, string quoteCurrency) => _reads.Count(read =>
        string.Equals(read.Base, baseCurrency, StringComparison.Ordinal) &&
        string.Equals(read.Quote, quoteCurrency, StringComparison.Ordinal));

    public Task<Deals.Models.Entities.FxRate?> GetRateAsync(
        string baseCurrency,
        string quoteCurrency,
        CancellationToken cancellationToken)
    {
        FetchedAnything = true;
        throw new InvalidOperationException("The bundle catalog must never fetch a rate.");
    }

    public Task<Deals.Models.Entities.FxRate?> GetLatestRateAsync(
        string baseCurrency,
        string quoteCurrency,
        CancellationToken cancellationToken)
    {
        _reads.Add((baseCurrency, quoteCurrency));
        var pair = string.Equals(baseCurrency, "USD", StringComparison.Ordinal)
            ? usdToMxn
            : mxnToUsd;
        Deals.Models.Entities.FxRate? row = pair is null
            ? null
            : new Deals.Models.Entities.FxRate
            {
                Base = baseCurrency,
                Quote = quoteCurrency,
                Rate = pair.Rate,
                RateDate = pair.RateDate,
                Source = pair.Source
            };
        return Task.FromResult(row);
    }
}
