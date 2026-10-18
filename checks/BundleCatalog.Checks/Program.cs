using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Itad;
using Deals.BusinessLogic.Models.Steam;
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
