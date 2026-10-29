// Deterministic checks for the bundle purchase/import contract (odd/tasks/bundle-expiry-acquisition.md,
// specs S3-S6). Run with:
//   dotnet run --project checks/BundlePurchase.Checks
//
// Every decision of the feature lives in BundlePurchaseRules (Deals.BusinessLogic/Models/Bundles/
// BundlePurchase.cs), so the whole decision surface is exercised here with plain records: no database, no
// HTTP, no credentials. What is *not* provable here is named at the end of the file: the advisory-lock
// order, the unique violation race of two concurrent confirms, the real FK cascade and the manual
// migration need a real PostgreSQL. The build, SQL/checks/bundle_purchases.sql and the migration are the
// proportionate evidence for those.
//
// A thrown Exception or a non-zero exit marks the suite failed; every case is collected first so one broken
// rule does not hide the others.

using Deals.BusinessLogic.Models.Bundles;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Steam;

var failures = new List<string>();

void Check(bool value, string name)
{
    if (!value) failures.Add(name);
}

void CheckEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        failures.Add($"{name}: expected '{expected}', got '{actual}'");
}

void CheckThrows<TException>(Action action, string name) where TException : Exception
{
    try
    {
        action();
        failures.Add($"{name}: expected {typeof(TException).Name}, nothing was thrown");
    }
    catch (TException)
    {
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: expected {typeof(TException).Name}, got {exception.GetType().Name}: {exception.Message}");
    }
}

// --- Fixtures ---------------------------------------------------------------------------------------

static string Item(string title, string? type = "game", string? itad = null, string? url = null) =>
    $$"""{"title":{{Str(title)}},"type":{{Str(type)}},"priceMinor":1000,"priceCurrency":"USD","itadGameId":{{Str(itad)}},"url":{{Str(url)}}}""";

static string? Str(string? value) => value is null ? "null" : "\"" + value + "\"";

static string Tier(
    string games,
    int? priceMinor = 1499,
    string? currency = "USD",
    bool addon = false,
    bool itemsComplete = true,
    int? selectionCount = null) =>
    $$"""{"priceMinor":{{(priceMinor?.ToString() ?? "null")}},"currency":{{Str(currency)}},"addon":{{Bool(addon)}},"itemsComplete":{{Bool(itemsComplete)}},"games":{{games}},"selectionCount":{{(selectionCount?.ToString() ?? "null")}}}""";

static string Bool(bool value) => value ? "true" : "false";

static string Tiers(params string[] tiers) => "[" + string.Join(",", tiers) + "]";

static string Games(params string[] games) => "[" + string.Join(",", games) + "]";

static BundlePurchaseSnapshotRow Snapshot(
    string tiersJson,
    string source = "itad",
    string bundleKey = "bundle-1",
    string title = "Prestige Collection",
    DateTime? expiresAt = null) =>
    new(
        Source: source,
        BundleKey: bundleKey,
        Title: title,
        ShopId: "4",
        ShopName: "Fanatical",
        PageUrl: "https://isthereanydeal.com/bundle/1",
        DealUrl: "https://www.fanatical.com/es/bundle/1?ref=itad",
        Details: "Bundles de Fanatical",
        PublishedAt: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        ExpiresAt: expiresAt,
        ObservedAt: new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc),
        TiersJson: tiersJson);

var today = new DateOnly(2026, 10, 20);
var itadUuidA = "7b1b2f14-0000-4000-8000-00000000000a";
var itadUuidB = "7b1b2f14-0000-4000-8000-00000000000b";
var itadUuidMissing = "7b1b2f14-0000-4000-8000-0000000000ff";

// A fixed tier (no selectionCount): every listed game is included. One DLC proves the import only takes
// plain games, and one item without an ITAD UUID proves the manual-link path.
var fixedGames = Games(
    Item("Alpha", itad: itadUuidA),
    Item("Beta", itad: itadUuidB),
    Item("Gamma", itad: null),
    Item("Alpha Soundtrack", type: "dlc", itad: "7b1b2f14-0000-4000-8000-0000000000dd"));
var fixedSnapshot = Snapshot(Tiers(Tier(fixedGames)));

// Same bundle, but the provider published a "choose 2 of 4" rule.
var choiceSnapshot = Snapshot(Tiers(Tier(fixedGames, priceMinor: 999, selectionCount: 2)), bundleKey: "bundle-choice");

// Incomplete tier: the provider did not list every item, so it must never import.
var incompleteSnapshot = Snapshot(Tiers(Tier(fixedGames, itemsComplete: false)), bundleKey: "bundle-incomplete");

// Addon tier: a bundle of add-ons has no game membership to import.
var addonSnapshot = Snapshot(Tiers(Tier(fixedGames, addon: true)), bundleKey: "bundle-addon");

// No eligible game at all.
var noGamesSnapshot = Snapshot(
    Tiers(Tier(Games(Item("Dlc", type: "dlc")))),
    bundleKey: "bundle-dlc");

// --- Snapshot hash and stable keys (S3/S6) ----------------------------------------------------------

var hash = BundlePurchaseRules.ComputeSnapshotHash(fixedSnapshot);
CheckEqual(64, hash.Length, "snapshot hash is a 64-char sha256 hex");
Check(hash.All(c => "0123456789abcdef".Contains(c)), "snapshot hash is lowercase hex");
CheckEqual(hash, BundlePurchaseRules.ComputeSnapshotHash(fixedSnapshot), "snapshot hash is deterministic");
CheckEqual(
    hash,
    BundlePurchaseRules.ComputeSnapshotHash(Snapshot(Tiers(Tier(fixedGames)))),
    "snapshot hash ignores how the same provider payload was stored");

Check(
    hash != BundlePurchaseRules.ComputeSnapshotHash(Snapshot(Tiers(Tier(fixedGames, priceMinor: 1499)), title: "Otra colección")),
    "snapshot hash changes when the bundle title changes");
Check(
    hash != BundlePurchaseRules.ComputeSnapshotHash(Snapshot(Tiers(Tier(fixedGames, priceMinor: 1498)))),
    "snapshot hash changes when the tier price changes");
Check(
    hash != BundlePurchaseRules.ComputeSnapshotHash(Snapshot(Tiers(Tier(Games(Item("Alpha", itad: itadUuidA)))))),
    "snapshot hash changes when tier membership changes");
Check(
    hash != BundlePurchaseRules.ComputeSnapshotHash(fixedSnapshot with { ExpiresAt = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc) }),
    "snapshot hash changes when the expiry changes");

var fixedTiers = BundlePurchaseRules.ParseTiers(fixedSnapshot.TiersJson);
CheckEqual(1, fixedTiers.Count, "the persisted tier payload parses into tiers");
var tierKey = BundlePurchaseRules.ComputeTierKey(0, fixedTiers[0]);
CheckEqual(64, tierKey.Length, "tier key is a 64-char sha256 hex");
CheckEqual(tierKey, BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(fixedSnapshot.TiersJson)[0]), "tier key is deterministic");
Check(
    tierKey != BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
    "tier key changes when the tier content changes");
Check(
    tierKey != BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(incompleteSnapshot.TiersJson)[0]),
    "tier key changes when completeness changes");
Check(
    tierKey == BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(
        Snapshot(Tiers(Tier(fixedGames)), title: "Otro título del bundle").TiersJson)[0]),
    "tier key stays stable when only the bundle title changes");

var alphaItem = fixedTiers[0].Games[0];
CheckEqual(
    "itad:" + itadUuidA,
    BundlePurchaseRules.ComputeItemKey(alphaItem),
    "an ITAD-identified item is keyed by its UUID, never by its title");
var gammaItem = fixedTiers[0].Games[2];
var gammaKey = BundlePurchaseRules.ComputeItemKey(gammaItem);
Check(gammaKey.StartsWith("item:", StringComparison.Ordinal), "an item without an ITAD id is keyed by content");
CheckEqual(gammaKey, BundlePurchaseRules.ComputeItemKey(gammaItem), "the content key is deterministic");
Check(
    gammaKey != BundlePurchaseRules.ComputeItemKey(fixedTiers[0].Games[3]),
    "two different items never share a key");

// --- Preview (S4) -----------------------------------------------------------------------------------

var itadCanonical = new Dictionary<string, long>(StringComparer.Ordinal)
{
    [itadUuidA] = 11,
    [itadUuidB] = 12
};
var owned = new List<BundlePurchaseOwnedPair>
{
    new("steam", 11, 45),
    new("gog", 12, 77)
};

var preview = BundlePurchaseRules.BuildPreview(fixedSnapshot, "steam", itadCanonical, owned);
CheckEqual(hash, preview.SnapshotHash, "preview publishes the snapshot hash the confirm must repeat");
CheckEqual(1, preview.Tiers.Count, "preview lists every tier of the snapshot");

var fixedTier = preview.Tiers[0];
CheckEqual(BundlePurchaseSelectionModes.Fixed, fixedTier.SelectionMode, "a tier with no selectionCount is a fixed tier");
CheckEqual(3, fixedTier.RequiredSelectionCount, "a fixed tier requires every eligible game");
Check(fixedTier.Importable, "a complete fixed tier is importable");
CheckEqual<string?>(null, fixedTier.BlockedReason, "an importable tier has no blocked reason");
CheckEqual(tierKey, fixedTier.TierKey, "preview republishes the same tier key");
CheckEqual(4, fixedTier.Items.Count, "preview lists the whole tier membership, DLC included");

var alphaPreview = fixedTier.Items.Single(item => item.ItemKey == "itad:" + itadUuidA);
Check(alphaPreview.Eligible, "a plain game is eligible");
CheckEqual<long?>(11, alphaPreview.CanonicalGameId, "an already-mapped ITAD item shows its canonical id");
CheckEqual<string?>(BundlePurchaseCanonicalSources.Itad, alphaPreview.CanonicalSource, "an ITAD item resolves through the itad namespace");
Check(!alphaPreview.RequiresManualLink, "an ITAD-identified item never needs a manual link");
CheckEqual<bool?>(true, alphaPreview.Owned, "the item is owned on the requested platform");
CheckEqual<long?>(45, alphaPreview.OwnedUserLibraryId, "the owned row id is published");
CheckEqual<string?>("steam", alphaPreview.OwnedPlatform, "the owned platform is published");

var betaPreview = fixedTier.Items.Single(item => item.ItemKey == "itad:" + itadUuidB);
CheckEqual<bool?>(false, betaPreview.Owned, "ownership is per platform: a GOG copy is not a Steam copy");

var gammaPreview = fixedTier.Items.Single(item => item.ItemKey == gammaKey);
Check(gammaPreview.RequiresManualLink, "an item without an ITAD id requires an explicit canonical link");
CheckEqual<long?>(null, gammaPreview.CanonicalGameId, "no canonical id is invented for an identity-less item");
CheckEqual<string?>(null, gammaPreview.CanonicalSource, "an identity-less item has no canonical source until it is linked");
Check(gammaPreview.Eligible, "the identity-less item is still an eligible game");

var dlcPreview = fixedTier.Items[3];
Check(!dlcPreview.Eligible, "a DLC is not eligible for library import");

var unmappedSnapshot = Snapshot(Tiers(Tier(Games(Item("Unknown", itad: itadUuidMissing)))), bundleKey: "bundle-unknown");
var unmappedPreview = BundlePurchaseRules.BuildPreview(unmappedSnapshot, "steam", itadCanonical, owned);
var unmappedItem = unmappedPreview.Tiers[0].Items[0];
CheckEqual<string?>(BundlePurchaseCanonicalSources.Itad, unmappedItem.CanonicalSource, "an unmapped ITAD item still resolves through the itad namespace");
CheckEqual<long?>(null, unmappedItem.CanonicalGameId, "an unmapped ITAD item has no canonical id yet");
Check(!unmappedItem.RequiresManualLink, "an ITAD UUID is reliable identity even before the canonical row exists");

var noPlatformPreview = BundlePurchaseRules.BuildPreview(fixedSnapshot, null, itadCanonical, owned);
CheckEqual<bool?>(null, noPlatformPreview.Tiers[0].Items[0].Owned, "without a platform ownership is unknown, not false");
CheckEqual<string?>(null, noPlatformPreview.Tiers[0].Items[0].OwnedPlatform, "without a platform no owned platform is published");

var choicePreview = BundlePurchaseRules.BuildPreview(choiceSnapshot, "steam", itadCanonical, owned);
CheckEqual(BundlePurchaseSelectionModes.Choice, choicePreview.Tiers[0].SelectionMode, "a tier with selectionCount is a choice tier");
CheckEqual(2, choicePreview.Tiers[0].RequiredSelectionCount, "a choice tier requires exactly the provider count");
Check(choicePreview.Tiers[0].Importable, "a choice tier with a valid count is importable");

var incompletePreview = BundlePurchaseRules.BuildPreview(incompleteSnapshot, "steam", itadCanonical, owned);
Check(!incompletePreview.Tiers[0].Importable, "an incomplete tier is not importable");
CheckEqual<string?>(BundlePurchaseBlockedReasons.ItemsIncomplete, incompletePreview.Tiers[0].BlockedReason, "an incomplete tier states items_incomplete");

var addonPreview = BundlePurchaseRules.BuildPreview(addonSnapshot, "steam", itadCanonical, owned);
CheckEqual<string?>(BundlePurchaseBlockedReasons.AddonTier, addonPreview.Tiers[0].BlockedReason, "an addon tier states addon_tier");

var noGamesPreview = BundlePurchaseRules.BuildPreview(noGamesSnapshot, "steam", itadCanonical, owned);
CheckEqual<string?>(BundlePurchaseBlockedReasons.NoEligibleGames, noGamesPreview.Tiers[0].BlockedReason, "a tier without plain games states no_eligible_games");
Check(!noGamesPreview.Tiers[0].Importable, "a tier without plain games is not importable");

var invalidChoiceSnapshot = Snapshot(
    Tiers(Tier(Games(Item("A", itad: itadUuidA), Item("B", itad: itadUuidB)), selectionCount: 5)),
    bundleKey: "bundle-invalid-choice");
var invalidChoicePreview = BundlePurchaseRules.BuildPreview(invalidChoiceSnapshot, "steam", itadCanonical, owned);
CheckEqual<string?>(BundlePurchaseBlockedReasons.InvalidSelectionCount, invalidChoicePreview.Tiers[0].BlockedReason, "a selection count above the membership states invalid_selection_count");
Check(!invalidChoicePreview.Tiers[0].Importable, "an impossible selection count is not importable");

// Duplicate content inside one tier must still produce two distinct preview keys.
var collisionSnapshot = Snapshot(
    Tiers(Tier(Games(Item("Same"), Item("Same")))),
    bundleKey: "bundle-collision");
var collisionItems = BundlePurchaseRules.BuildPreview(collisionSnapshot, "steam", itadCanonical, owned).Tiers[0].Items;
CheckEqual(2, collisionItems.Select(item => item.ItemKey).Distinct(StringComparer.Ordinal).Count(), "two identical identity-less items get distinct keys");

// --- Confirm: request shape and staleness (S3/S4/S6) ------------------------------------------------

var fixedRequest = new BundlePurchaseRequest(
    Source: "itad",
    BundleKey: "bundle-1",
    TierKey: tierKey,
    Platform: " Steam ",
    SnapshotHash: hash,
    AcquiredAt: new DateOnly(2026, 10, 18),
    AcquiredStore: " Fanatical ",
    AcquiredStoreType: " official ",
    AcquisitionProvider: " itad ",
    // A fixed tier may omit the selection, but this snapshot contains an item without an ITAD id, so the
    // person has to send the whole membership with the manual link for that item.
    Items:
    [
        new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
        new BundlePurchaseItemSelection("itad:" + itadUuidB, null, null),
        new BundlePurchaseItemSelection(gammaKey, null, 321)
    ]);
var existingCanonical = new HashSet<long> { 321 };
var fixedPlan = BundlePurchaseRules.Plan(fixedRequest, fixedSnapshot, 8, today, existingCanonical);

CheckEqual("steam", fixedPlan.Platform, "the tier platform is normalized once");
CheckEqual("fanatical", fixedPlan.AcquiredStore, "the acquisition store is normalized");
CheckEqual<string?>(WishlistAcquiredStoreTypes.Official, fixedPlan.AcquiredStoreType, "the store classification is normalized");
CheckEqual<string?>(WishlistAcquisitionProviders.Itad, fixedPlan.AcquisitionProvider, "the discovery provider is normalized");
CheckEqual<DateOnly?>(new DateOnly(2026, 10, 18), fixedPlan.AcquiredAt, "the acquisition date is preserved");
CheckEqual(BundlePurchaseSelectionModes.Fixed, fixedPlan.SelectionMode, "a fixed tier plans as fixed");
CheckEqual(3, fixedPlan.SelectedCount, "a fixed tier plans every eligible game");
CheckEqual(hash, fixedPlan.SnapshotHash, "the plan carries the validated snapshot hash");
CheckEqual(64, fixedPlan.SnapshotJson.Length > 0 ? 64 : 0, "the plan carries the frozen provider snapshot");
Check(fixedPlan.SnapshotJson.Contains(itadUuidA, StringComparison.Ordinal), "the frozen snapshot keeps the actual items");
CheckEqual(tierKey, fixedPlan.TierKey, "the plan repeats the tier key it validated");
CheckEqual(0, fixedPlan.TierIndex, "the plan records the tier position");

var plannedAlpha = fixedPlan.Items.Single(item => item.ItemKey == "itad:" + itadUuidA);
CheckEqual(BundlePurchaseCanonicalSources.Itad, plannedAlpha.CanonicalSource, "an ITAD item plans through the itad namespace");
CheckEqual<long?>(null, plannedAlpha.CanonicalGameId, "the ITAD canonical id is resolved, not invented, at execution");
CheckEqual("steam", plannedAlpha.Platform, "an item without override takes the tier platform");

var plannedGamma = fixedPlan.Items.Single(item => item.ItemKey == gammaKey);
CheckEqual(BundlePurchaseCanonicalSources.Manual, plannedGamma.CanonicalSource, "an identity-less fixed-tier item keeps its manual link");
CheckEqual<long?>(321, plannedGamma.CanonicalGameId, "the manual link is the canonical id the person chose");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { Items = null }, fixedSnapshot, 8, today, existingCanonical),
    "a fixed tier with an identity-less item cannot be confirmed without its manual link");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        fixedRequest with
        {
            Items = fixedPlan.Items
                .Where(item => item.ItemKey != gammaKey)
                .Select(item => new BundlePurchaseItemSelection(item.ItemKey, null, null))
                .ToList()
        },
        fixedSnapshot, 8, today, existingCanonical),
    "a fixed tier refuses a partial selection");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        fixedRequest with
        {
            Items = fixedPlan.Items
                .Select(item => new BundlePurchaseItemSelection(item.ItemKey, null, null))
                .Append(new BundlePurchaseItemSelection(gammaKey, null, null))
                .ToList()
        },
        fixedSnapshot, 8, today, existingCanonical),
    "a fixed tier refuses a duplicated selection key");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        fixedRequest with
        {
            Items = fixedPlan.Items
                .Select(item => new BundlePurchaseItemSelection(item.ItemKey, null, null))
                .Append(new BundlePurchaseItemSelection("itad:" + itadUuidMissing, null, null))
                .ToList()
        },
        fixedSnapshot, 8, today, existingCanonical),
    "a selection key outside the tier is refused");

CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { SnapshotHash = new string('0', 64) }, fixedSnapshot, 8, today, existingCanonical),
    "a stale or tampered snapshot hash is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { SnapshotHash = " " }, fixedSnapshot, 8, today, existingCanonical),
    "a blank snapshot hash is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { TierKey = new string('0', 64) }, fixedSnapshot, 8, today, existingCanonical),
    "an unknown tier key is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { Source = "ggdeals" }, fixedSnapshot, 8, today, existingCanonical),
    "a source that does not match the snapshot is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { BundleKey = "otro" }, fixedSnapshot, 8, today, existingCanonical),
    "a bundle key that does not match the snapshot is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { Platform = " " }, fixedSnapshot, 8, today, existingCanonical),
    "a blank tier platform is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { AcquiredAt = today.AddDays(1) }, fixedSnapshot, 8, today, existingCanonical),
    "a future acquisition date is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { AcquiredStore = "fanatical", AcquiredStoreType = "gratis" }, fixedSnapshot, 8, today, existingCanonical),
    "an unknown store classification is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest with { AcquiredStore = null, AcquisitionProvider = "itad" }, fixedSnapshot, 8, today, existingCanonical),
    "a source fact without the acquisition store is refused");

CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest, incompleteSnapshot with { BundleKey = "bundle-1" }, 8, today, existingCanonical),
    "an incomplete tier cannot be confirmed");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(fixedRequest, addonSnapshot with { BundleKey = "bundle-1" }, 8, today, existingCanonical),
    "an addon tier cannot be confirmed");

// --- Confirm: choice tiers and per-game overrides (S4) ----------------------------------------------

var choicePlan = BundlePurchaseRules.Plan(
    new BundlePurchaseRequest(
        "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
        "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
        [
            new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
            new BundlePurchaseItemSelection(gammaKey, " GOG ", 321)
        ]),
    choiceSnapshot, 8, today, existingCanonical);

CheckEqual(2, choicePlan.SelectedCount, "a choice tier accepts exactly the provider count");
CheckEqual(BundlePurchaseSelectionModes.Choice, choicePlan.SelectionMode, "the plan records a choice tier");
CheckEqual("gog", choicePlan.Items.Single(item => item.ItemKey == gammaKey).Platform, "a per-game platform override is normalized");
CheckEqual(BundlePurchaseCanonicalSources.Manual, choicePlan.Items.Single(item => item.ItemKey == gammaKey).CanonicalSource, "an identity-less item needs a manual canonical link");
CheckEqual<long?>(321, choicePlan.Items.Single(item => item.ItemKey == gammaKey).CanonicalGameId, "the manual canonical link is preserved");
CheckEqual("steam", choicePlan.Items.Single(item => item.ItemKey == "itad:" + itadUuidA).Platform, "an item without override keeps the tier platform");

CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null)]),
        choiceSnapshot, 8, today, existingCanonical),
    "a choice tier refuses fewer games than the provider count");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
                new BundlePurchaseItemSelection("itad:" + itadUuidB, null, null),
                new BundlePurchaseItemSelection(gammaKey, null, 321)
            ]),
        choiceSnapshot, 8, today, existingCanonical),
    "a choice tier refuses more games than the provider count");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null)
            ]),
        choiceSnapshot, 8, today, existingCanonical),
    "a choice tier refuses a repeated item key");

CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
                new BundlePurchaseItemSelection(fixedTiers[0].Games[3].Title, null, 321)
            ]),
        choiceSnapshot, 8, today, existingCanonical),
    "a selection key outside the tier is refused even in a choice tier");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
                new BundlePurchaseItemSelection("itad:" + "7b1b2f14-0000-4000-8000-0000000000dd", null, 321)
            ]),
        choiceSnapshot, 8, today, existingCanonical),
    "a DLC item cannot be selected for import");

// Manual identity rules: never from a title, never conflicting with an ITAD UUID.
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
                new BundlePurchaseItemSelection(gammaKey, null, null)
            ]),
        choiceSnapshot, 8, today, existingCanonical),
    "an identity-less item without a canonical link is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
                new BundlePurchaseItemSelection(gammaKey, null, 9999)
            ]),
        choiceSnapshot, 8, today, existingCanonical),
    "a manual link to a canonical game that does not exist is refused");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, 321),
                new BundlePurchaseItemSelection(gammaKey, null, 321)
            ]),
        choiceSnapshot, 8, today, existingCanonical),
    "an ITAD-identified item refuses a client canonical id");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad", "bundle-choice", BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(choiceSnapshot.TiersJson)[0]),
            "steam", BundlePurchaseRules.ComputeSnapshotHash(choiceSnapshot), null, null, null, null,
            [
                new BundlePurchaseItemSelection("itad:" + itadUuidA, null, null),
                new BundlePurchaseItemSelection(gammaKey, "not a platform!", 321)
            ]),
        choiceSnapshot, 8, today, existingCanonical),
    "an invalid per-game platform override is refused");

// --- Duplicate inventory and bookkeeping (S5) -------------------------------------------------------

CheckEqual(BundlePurchaseOutcomes.Imported, BundlePurchaseRules.OutcomeFor(alreadyOwned: false), "a new game on the platform imports");
CheckEqual(BundlePurchaseOutcomes.Duplicate, BundlePurchaseRules.OutcomeFor(alreadyOwned: true), "an owned game on the same platform records a duplicate");
CheckEqual("12", BundlePurchaseRules.StoreGameIdFor("gog", 12), "a non-Steam bundle import keys the library row on the canonical id");
CheckEqual("1057800", BundlePurchaseRules.StoreGameIdFor("steam", 12, "1057800"), "a known Steam appid is used verbatim, never replaced");
CheckEqual("bundle:12", BundlePurchaseRules.StoreGameIdFor("steam", 12), "a Steam import without a known appid never uses a bare canonical id");
Check(
    !int.TryParse(BundlePurchaseRules.StoreGameIdFor("steam", 12), out _),
    "the synthetic Steam key cannot be mistaken for an appid by the price binder");

Check(BundlePurchaseRules.HidesBundle("itad", "bundle-1", "itad", "bundle-1"), "the purchased bundle is hidden from its own source");
Check(BundlePurchaseRules.HidesBundle("itad", "bundle-1", "itad_global", "bundle-1"), "a purchase hides the same Itad bundle from the global sweep source");
Check(BundlePurchaseRules.HidesBundle("itad_global", "bundle-1", "itad", "bundle-1"), "family hiding is symmetric");
Check(!BundlePurchaseRules.HidesBundle("itad", "bundle-1", "itad", "bundle-2"), "another bundle is still visible");
Check(!BundlePurchaseRules.HidesBundle("itad", "bundle-1", "ggdeals", "bundle-1"), "a gg.deals bundle is never hidden by an Itad purchase");
Check(!BundlePurchaseRules.HidesBundle("ggdeals", "bundle-1", "itad", "bundle-1"), "an Itad bundle is never hidden by a gg.deals purchase");
Check(BundlePurchaseRules.HidesBundle("ggdeals", "bundle-1", "ggdeals", "bundle-1"), "a gg.deals purchase hides its own bundle");

// --- Steam wishlist removal with exclusion protection (S5) -------------------------------------------

var steamAppIds = new Dictionary<long, string>
{
    [11] = "1057800",
    [12] = "2000"
};
var wished = new HashSet<string>(StringComparer.Ordinal) { "1057800", "2000", "3000" };
var excluded = new HashSet<string>(StringComparer.Ordinal) { "3000" };

var wishlistPlan = BundlePurchaseRules.PlanWishlist([11, 12], steamAppIds, wished, excluded);
CheckEqual(2, wishlistPlan.Removals.Count, "every acquired game with a wished appid is removed");
CheckEqual("1057800,2000", string.Join(",", wishlistPlan.Removals), "removals are distinct and ordinal-sorted");
CheckEqual(2, wishlistPlan.NewExclusions.Count, "each removal gets a protection exclusion");

var partialWishlist = BundlePurchaseRules.PlanWishlist([11, 12], steamAppIds, new HashSet<string>(StringComparer.Ordinal) { "1057800" }, excluded);
CheckEqual(1, partialWishlist.Removals.Count, "a game that is not wished is not removed");
CheckEqual("1057800", string.Join(",", partialWishlist.Removals), "only the wished appid is removed");
CheckEqual(1, partialWishlist.NewExclusions.Count, "only the removed appid gets a new exclusion");

var alreadyProtected = BundlePurchaseRules.PlanWishlist([11, 12], steamAppIds, wished, new HashSet<string>(StringComparer.Ordinal) { "1057800", "2000" });
CheckEqual(2, alreadyProtected.Removals.Count, "an already-excluded appid is still removed once");
CheckEqual(0, alreadyProtected.NewExclusions.Count, "an existing exclusion is never duplicated");

var duplicateAlsoRemoved = BundlePurchaseRules.PlanWishlist([11, 11, 12], steamAppIds, wished, excluded);
CheckEqual(2, duplicateAlsoRemoved.Removals.Count, "a duplicated acquired game contributes one removal");

var unknownAppId = BundlePurchaseRules.PlanWishlist([99], steamAppIds, wished, excluded);
CheckEqual(0, unknownAppId.Removals.Count, "an acquired game without a Steam appid removes nothing");
CheckEqual(0, unknownAppId.NewExclusions.Count, "no appid is fabricated for a game without one");

// The hash covers the parsed payload, not the stored text: a provider rewrite that only reorders keys or
// re-indents the same JSON must not invalidate a preview the person is looking at.
var reformattedTiers = """
[
  {
    "addon": false,
    "currency": "USD",
    "games": [
      {"url": null, "itadGameId": "7b1b2f14-0000-4000-8000-00000000000a", "priceCurrency": "USD", "priceMinor": 1000, "type": "game", "title": "Alpha"},
      {"url": null, "itadGameId": "7b1b2f14-0000-4000-8000-00000000000b", "priceCurrency": "USD", "priceMinor": 1000, "type": "game", "title": "Beta"},
      {"url": null, "itadGameId": null, "priceCurrency": "USD", "priceMinor": 1000, "type": "game", "title": "Gamma"},
      {"url": null, "itadGameId": "7b1b2f14-0000-4000-8000-0000000000dd", "priceCurrency": "USD", "priceMinor": 1000, "type": "dlc", "title": "Alpha Soundtrack"}
    ],
    "itemsComplete": true,
    "priceMinor": 1499,
    "selectionCount": null
  }
]
""";
CheckEqual(
    hash,
    BundlePurchaseRules.ComputeSnapshotHash(Snapshot(reformattedTiers)),
    "the snapshot hash covers the parsed payload, not the stored JSON text");

// A blank provider title cannot become a canonical game name or a library row title.
var blankTitleSnapshot = Snapshot(
    Tiers(Tier(Games(Item("", itad: itadUuidA)))),
    bundleKey: "bundle-blank-title");
CheckThrows<ArgumentException>(
    () => BundlePurchaseRules.Plan(
        new BundlePurchaseRequest(
            "itad",
            "bundle-blank-title",
            BundlePurchaseRules.ComputeTierKey(0, BundlePurchaseRules.ParseTiers(blankTitleSnapshot.TiersJson)[0]),
            "steam",
            BundlePurchaseRules.ComputeSnapshotHash(blankTitleSnapshot),
            null, null, null, null, null),
        blankTitleSnapshot, 8, today, existingCanonical),
    "a tier item without a title is refused instead of importing an unnamed row");

Console.WriteLine($"BundlePurchase.Checks: {(failures.Count == 0 ? "PASS" : "FAIL")}");
foreach (var failure in failures) Console.WriteLine("  - " + failure);

// Not covered here on purpose, because they need a real PostgreSQL (SQL/checks/bundle_purchases.sql and
// SQL/migrations/2026-10-21_bundle_purchases.sql are the evidence for the schema side):
//   * the advisory-lock order (per-user wishlist gate then the global identity gate) and its 409 mapping;
//   * a genuine concurrent double confirm: the UNIQUE (user_id, source, bundle_key) constraint and the
//     lock serialization, which no in-memory record can prove;
//   * the FK cascades (purchase -> purchase games -> user_library SET NULL) and the CHECK constraints;
//   * that the migration applies to an existing database.
return failures.Count == 0 ? 0 : 1;
