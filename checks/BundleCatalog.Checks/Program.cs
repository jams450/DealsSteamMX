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
