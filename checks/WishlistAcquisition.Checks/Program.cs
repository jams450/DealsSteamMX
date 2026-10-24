// Deterministic checks for the wishlist → library acquisition (spec S1/S2/S3). Run with:
//   dotnet run --project checks/WishlistAcquisition.Checks
//
// Every rule of the feature that decides what is written lives in WishlistAcquisitionRules, so the whole
// decision surface is exercised here with plain records: no database, no HTTP, no credentials. What is
// *not* covered here is named in the file: persistence order, the FK cascade of the removed wishlist row
// and the exclusion filter of the Steam sync need a real PostgreSQL, and SQL/checks/wishlist-acquisition.sql
// plus the build are the proportionate evidence for those.
//
// A thrown Exception or a non-zero exit marks the suite failed; every case is collected first so one
// broken rule does not hide the others.

using System.Reflection;
using Deals.BusinessLogic.Models.Library;
using Deals.BusinessLogic.Models.Steam;
using Deals.BusinessLogic.Services;
using Deals.Models.Entities;

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

// --- Request validation (S5) ------------------------------------------------------------------------
// The platform and the purchase store are separate fields: one says which library the row belongs to,
// the other where it was bought. Both use the existing store vocabulary, so a known store key and an open
// platform slug are both valid, and nothing else is.

var today = new DateOnly(2026, 10, 18);
var validated = WishlistAcquisitionRules.Validate(
    new WishlistAcquisitionRequest(" Steam ", " GOG ", " PURCHASE ", new DateOnly(2024, 5, 1), false),
    today);
CheckEqual("steam", validated.Platform, "platform is trimmed and lowercased");
CheckEqual("gog", validated.AcquiredStore, "acquisition store is trimmed and lowercased");
CheckEqual("purchase", validated.AcquisitionType, "acquisition type is trimmed and lowercased");
CheckEqual<DateOnly?>(new DateOnly(2024, 5, 1), validated.AcquiredAt, "acquisition date is preserved");
Check(!validated.RemoveFromWishlist, "removeFromWishlist is preserved when false");

CheckEqual(
    "switch",
    WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("switch", "gamestop", "gift", null, true), today).Platform,
    "an open platform slug is a valid platform");

CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("  ", "gog", "purchase", null, false), today),
    "a blank platform is rejected");

CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("Steam Store", "gog", "purchase", null, false), today),
    "an unknown platform is rejected");

CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", null, "purchase", null, false), today),
    "a missing acquisition store is rejected");

CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "GOG Store!", "purchase", null, false), today),
    "an unknown acquisition store is rejected");

CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "gog", "rental", null, false), today),
    "an unknown acquisition type is rejected");

CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", new DateOnly(2026, 10, 19), false),
        today),
    "a future acquisition date is rejected");

CheckEqual<DateOnly?>(
    today,
    WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", today, false), today).AcquiredAt,
    "today is a valid acquisition date");

CheckThrows<ArgumentException>(() => WishlistAcquisitionRules.Validate(null, today), "a missing request is rejected");

// --- Shop classification and discovery provider are optional, explicit facts (S6/L14) ----------------
// `acquiredStore` stays the actual shop. The classification says whether that shop is official or a
// keyshop and the provider says who discovered the offer: two separate, manually chosen facts, never
// inferred from a price. Both are optional as a pair, so the previous payload (no such fields) keeps
// working and every row written before them keeps NULL, which is a real answer.

var classified = WishlistAcquisitionRules.Validate(
    new WishlistAcquisitionRequest(
        " Steam ", " GOG ", " PURCHASE ", new DateOnly(2024, 5, 1), false, " Official ", " iTaD "),
    today);
CheckEqual("official", classified.AcquiredStoreType, "the shop classification is trimmed and lowercased");
CheckEqual("itad", classified.AcquisitionProvider, "the discovery provider is trimmed and lowercased");
CheckEqual("gog", classified.AcquiredStore, "the classification does not replace the actual shop");
CheckEqual("purchase", classified.AcquisitionType, "the classification does not replace the acquisition type");

var noSource = WishlistAcquisitionRules.Validate(
    new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false),
    today);
CheckEqual<string?>(null, noSource.AcquiredStoreType, "a previous payload leaves the shop classification unset");
CheckEqual<string?>(null, noSource.AcquisitionProvider, "a previous payload leaves the provider unset");

var explicitNullSource = WishlistAcquisitionRules.Validate(
    new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false, null, null),
    today);
CheckEqual<string?>(null, explicitNullSource.AcquiredStoreType, "an explicit null classification is the same as absent");
CheckEqual<string?>(null, explicitNullSource.AcquisitionProvider, "an explicit null provider is the same as absent");

CheckEqual<string?>(
    "keyshop",
    WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false, "keyshop", null), today)
        .AcquiredStoreType,
    "the classification may be sent without a provider");
CheckEqual<string?>(
    "other",
    WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false, null, "other"), today)
        .AcquisitionProvider,
    "the provider may be sent without a classification");

// The two vocabularies are closed, in the contract order, and mirrored by the SQL CHECKs.
foreach (var value in WishlistAcquiredStoreTypes.All)
{
    Check(WishlistAcquiredStoreTypes.IsKnown(value), $"{value} is part of the shop classification vocabulary");
}
CheckEqual(3, WishlistAcquiredStoreTypes.All.Count, "the classification vocabulary is official, keyshop and unknown");
CheckEqual(
    "official, keyshop, unknown",
    string.Join(", ", WishlistAcquiredStoreTypes.All),
    "the classification vocabulary keeps its contract order");
Check(!WishlistAcquiredStoreTypes.IsKnown("grey"), "an unlisted classification is not known");
Check(!WishlistAcquiredStoreTypes.IsKnown(null), "a missing classification is not known");
CheckEqual<string?>(
    null,
    WishlistAcquiredStoreTypes.Normalize(" grey-market "),
    "an unknown classification normalizes to null instead of being stored");

foreach (var value in WishlistAcquisitionProviders.All)
{
    Check(WishlistAcquisitionProviders.IsKnown(value), $"{value} is part of the provider vocabulary");
}
CheckEqual(4, WishlistAcquisitionProviders.All.Count, "the provider vocabulary is itad, ggdeals, direct and other");
CheckEqual(
    "itad, ggdeals, direct, other",
    string.Join(", ", WishlistAcquisitionProviders.All),
    "the provider vocabulary keeps its contract order");
Check(!WishlistAcquisitionProviders.IsKnown("cheapshark"), "an unlisted provider is not known");
CheckEqual<string?>(
    null,
    WishlistAcquisitionProviders.Normalize(" steam "),
    "a shop name is not a provider");

// A non-null value outside the vocabulary is a 400, never a stored typo. Absent/null is the only way to
// say "not provided", so a blank string is rejected instead of being silently read as absent.
CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false, "grey", null), today),
    "an unknown shop classification is rejected");
CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false, "official", "cheapshark"), today),
    "an unknown discovery provider is rejected");
CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false, "   ", null), today),
    "a blank classification is rejected instead of being read as absent");
CheckThrows<ArgumentException>(
    () => WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false, null, ""), today),
    "a blank provider is rejected instead of being read as absent");

// Previous callers keep compiling: the two new fields are optional parameters, so a five-positional
// request (the old shape) still constructs and the new facts stay null.
var requestConstructor = typeof(WishlistAcquisitionRequest).GetConstructors().Single();
var sourceParameters = requestConstructor.GetParameters().Skip(5).ToList();
CheckEqual(2, sourceParameters.Count, "the request record gained exactly the two source facts");
foreach (var parameter in sourceParameters)
{
    Check(
        parameter.HasDefaultValue && parameter.DefaultValue is null,
        $"WishlistAcquisitionRequest.{parameter.Name} defaults to null so previous callers still compile");
    Check(
        !parameter.ParameterType.IsValueType || Nullable.GetUnderlyingType(parameter.ParameterType) is not null,
        $"WishlistAcquisitionRequest.{parameter.Name} is nullable");
}

// The five contract values are the whole vocabulary; everything else is a 400 instead of a stored typo.
foreach (var type in WishlistAcquisitionTypes.All)
{
    Check(WishlistAcquisitionTypes.IsKnown(type), $"{type} is part of the acquisition vocabulary");
}
Check(!WishlistAcquisitionTypes.IsKnown("rental"), "rental is not part of the acquisition vocabulary");
Check(!WishlistAcquisitionTypes.IsKnown(null), "a missing acquisition type is not known");

// --- Subscription means a subscription row (S1) ------------------------------------------------------
CheckEqual("subscription", WishlistAcquisitionRules.StateFor("subscription"), "subscription type writes state subscription");
foreach (var type in new[] { "purchase", "gift", "free", "bundle" })
{
    CheckEqual("owned", WishlistAcquisitionRules.StateFor(type), $"{type} writes state owned");
}

CheckThrows<ArgumentException>(() => WishlistAcquisitionRules.StateFor("rental"), "an unknown type has no state");

// --- Canonical identity: exact ids only (S1) --------------------------------------------------------
// Priority: the claimed Steam mapping is the identity truth (a merge repoints it), the snapshot backfill
// is next, and the wishlist row's own game_id is the last resort. A title never takes part.
CheckEqual<long?>(5L, WishlistAcquisitionRules.ResolveCanonicalGameId(5, 7, 9), "the claimed mapping wins");
CheckEqual<long?>(7L, WishlistAcquisitionRules.ResolveCanonicalGameId(null, 7, 9), "the snapshot fills a missing mapping");
CheckEqual<long?>(9L, WishlistAcquisitionRules.ResolveCanonicalGameId(null, null, 9), "the wishlist row is the last resort");
CheckEqual<long?>(null, WishlistAcquisitionRules.ResolveCanonicalGameId(null, null, null), "no exact id means no identity");

// --- store_game_id: a real store id when known, never a claim (S2) ---------------------------------
// The wishlist row's own store id is exact by construction (the Steam appid). Any other platform only
// has an exact id when one is already claimed in game_external_ids; otherwise the row takes the manual
// shape (the canonical id), which is what the manual and console imports already write.
CheckEqual(
    "1057800",
    WishlistAcquisitionRules.ResolveStoreGameId("steam", 42, "steam", "1057800", []),
    "the wishlist row's own store id is used verbatim");

CheckEqual(
    "aaa",
    WishlistAcquisitionRules.ResolveStoreGameId("epic", 42, "steam", "1057800", ["zzz", "aaa"]),
    "an existing exact store id is preferred over the canonical id, in ordinal order");

CheckEqual(
    "gog-slug",
    WishlistAcquisitionRules.ResolveStoreGameId("gog", 42, "steam", "1057800", ["", "  ", "gog-slug"]),
    "blank exact ids are ignored");

CheckEqual(
    "42",
    WishlistAcquisitionRules.ResolveStoreGameId("switch", 42, "steam", "1057800", []),
    "with no exact id the row takes the canonical id, like a manual row");

CheckEqual(
    "42",
    WishlistAcquisitionRules.ResolveStoreGameId("epic", 42, "steam", "1057800", ["42"]),
    "an existing mapping equal to the canonical id is still an exact existing id");

// A canonical game id is never written as an external id in a real store namespace: the only claim this
// path may make is the wishlist row's own exact store id.
var steamClaim = WishlistAcquisitionRules.ClaimFor("steam", "steam", "1057800");
Check(steamClaim is { Namespace: "steam", ExternalId: "1057800" }, "the Steam acquisition claims the exact appid");
Check(WishlistAcquisitionRules.ClaimFor("epic", "steam", "1057800") is null, "a foreign platform claims no id");
Check(WishlistAcquisitionRules.ClaimFor("switch", "steam", "1057800") is null, "a console platform claims no id");

// --- Plan: create, idempotency, metadata and dismissal (S2/S3) ------------------------------------
var now = new DateTime(2026, 10, 18, 15, 30, 0, DateTimeKind.Utc);

WishlistAcquisitionFacts Facts(
    WishlistAcquisitionExistingRow? existing = null,
    IReadOnlyList<string>? externalIds = null,
    bool alreadyExcluded = false,
    string title = "Floppy Knights") =>
    new("steam", "1057800", 42L, title, existing, externalIds ?? [], alreadyExcluded);

var purchase = WishlistAcquisitionRules.Validate(
    new WishlistAcquisitionRequest("steam", "gog", "purchase", new DateOnly(2024, 5, 1), false), today);
var created = WishlistAcquisitionRules.Plan(purchase, Facts(), now);
CheckEqual(WishlistAcquisitionOutcomes.Created, created.Outcome, "a new acquisition is created");
Check(created.CreateRow, "a new acquisition writes the library row");
CheckEqual(0L, created.UserLibraryId, "the row id is unknown until the insert");
CheckEqual(42L, created.GameId, "the plan carries the canonical game id");
CheckEqual("steam", created.Store, "the row lives in the chosen platform");
CheckEqual("1057800", created.StoreGameId, "the Steam row keeps the appid as its store id");
CheckEqual("owned", created.State, "a purchase writes state owned");
CheckEqual("gog", created.AcquiredStore, "the purchase store is stored separately from the platform");
CheckEqual("purchase", created.AcquisitionType, "the acquisition type is stored");
CheckEqual<DateOnly?>(new DateOnly(2024, 5, 1), created.AcquisitionDate, "the acquisition date is stored");
CheckEqual(new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), created.AddedAt, "a dated acquisition dates the row");
Check(!created.Dismiss, "the wishlist row stays when the user did not ask to remove it");
Check(!created.InsertExclusion, "no exclusion is written when the wishlist row stays");
Check(!created.FillAcquisitionMetadata, "a created row has nothing to fill");
CheckEqual<string?>(null, created.AcquiredStoreType, "a created row with no classification writes NULL");
CheckEqual<string?>(null, created.AcquisitionProvider, "a created row with no provider writes NULL");

// A new row carries the two facts the person chose, next to the actual shop they describe.
var classifiedCreated = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false, "keyshop", "ggdeals"), today),
    Facts(),
    now);
CheckEqual("gog", classifiedCreated.AcquiredStore, "the actual shop is still stored as the acquisition store");
CheckEqual("keyshop", classifiedCreated.AcquiredStoreType, "a new row carries the chosen shop classification");
CheckEqual("ggdeals", classifiedCreated.AcquisitionProvider, "a new row carries the chosen discovery provider");

var undated = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "steam", "purchase", null, false), today),
    Facts(),
    now);
CheckEqual<DateOnly?>(null, undated.AcquisitionDate, "an undated acquisition stores no date instead of inventing one");
CheckEqual(now, undated.AddedAt, "an undated acquisition is added now");

var subscription = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("xbox", "xbox", "subscription", null, false), today),
    Facts(),
    now);
CheckEqual("subscription", subscription.State, "a subscription acquisition writes state subscription");

// Already owned: the second submit must never create a duplicate row, and it must not rewrite what an
// earlier acquisition (or the import) recorded.
var ownedRow = new WishlistAcquisitionExistingRow(77L, "owned", HasAcquisitionMetadata: false);
var alreadyOwned = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "humble", "gift", null, false), today),
    Facts(ownedRow),
    now);
CheckEqual(WishlistAcquisitionOutcomes.AlreadyOwned, alreadyOwned.Outcome, "an owned game is already owned");
Check(!alreadyOwned.CreateRow, "an owned game writes no second row");
CheckEqual(77L, alreadyOwned.UserLibraryId, "an owned game reports the existing row");
CheckEqual("owned", alreadyOwned.State, "the existing state is not rewritten");
Check(alreadyOwned.FillAcquisitionMetadata, "an imported row with no acquisition metadata gets it filled once");

var repeat = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "humble", "gift", null, false), today),
    Facts(ownedRow with { HasAcquisitionMetadata = true }),
    now);
Check(!repeat.FillAcquisitionMetadata, "a repeated submit preserves the metadata already stored");
Check(!repeat.CreateRow, "a repeated submit writes no row");

// The classification and the provider are part of the same metadata unit, so fill-once applies to them as
// a whole: a repeat never attaches a new fact to an acquisition that was already recorded, and it never
// back-fills a value the first submit did not know. Guessing a historical classification is exactly what
// this rule forbids.
var repeatWithSource = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "humble", "gift", null, false, "keyshop", "ggdeals"), today),
    Facts(ownedRow with { HasAcquisitionMetadata = true }),
    now);
Check(
    !repeatWithSource.FillAcquisitionMetadata,
    "a repeated submit does not attach new source facts to an established acquisition");

var filledSource = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(
        new WishlistAcquisitionRequest("steam", "humble", "gift", null, false, "official", "direct"), today),
    Facts(ownedRow),
    now);
Check(filledSource.FillAcquisitionMetadata, "an imported row with no metadata is still filled once");
CheckEqual("official", filledSource.AcquiredStoreType, "the first fill writes the classification with the same unit");
CheckEqual("direct", filledSource.AcquisitionProvider, "the first fill writes the provider with the same unit");

// Removing from the wishlist is local and remembered: the row goes, and an exclusion keeps the next
// Steam sync from re-inserting it.
var dismiss = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "gog", "purchase", null, true), today),
    Facts(),
    now);
Check(dismiss.Dismiss, "the wished row is removed when the user asks for it");
Check(dismiss.InsertExclusion, "the removal is remembered with an exclusion");

var repeatDismiss = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "gog", "purchase", null, true), today),
    Facts(alreadyExcluded: true),
    now);
Check(repeatDismiss.Dismiss, "the second submit still reports the removal");
Check(!repeatDismiss.InsertExclusion, "a repeated submit writes no second exclusion");

var retain = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false), today),
    Facts(),
    now);
Check(!retain.Dismiss && !retain.InsertExclusion, "a retained wishlist row is neither removed nor excluded");

// user_library.title is VARCHAR(256): a longer store name is truncated here instead of failing the insert.
var longTitle = new string('x', 300);
var truncated = WishlistAcquisitionRules.Plan(
    WishlistAcquisitionRules.Validate(new WishlistAcquisitionRequest("steam", "gog", "purchase", null, false), today),
    Facts(title: longTitle),
    now);
CheckEqual(WishlistAcquisitionRules.MaxTitleLength, truncated.Title.Length, "an oversized title is truncated to the column width");
CheckEqual("Floppy Knights", created.Title, "a normal title is stored verbatim");

// --- The sync honours the exclusion (S3) ---------------------------------------------------------
// Steam keeps returning the appid until the person removes it there, so the filter is what makes the local
// removal survive: the excluded entry is dropped before any row is written or pruned.
var steamItems = new List<SteamWishlistItem>
{
    new(1057800, 1, null),
    new(990080, 2, null)
};
var kept = WishlistSyncService.WithoutExcludedItems(steamItems, new HashSet<string>(["1057800"], StringComparer.Ordinal));
CheckEqual(1, kept.Count, "the suppressed appid is dropped from the sync payload");
CheckEqual(990080, kept[0].AppId, "the entries that were not removed still sync");
CheckEqual(
    steamItems.Count,
    WishlistSyncService.WithoutExcludedItems(steamItems, new HashSet<string>(StringComparer.Ordinal)).Count,
    "with no exclusion the payload is untouched");
CheckEqual(
    steamItems.Count,
    WishlistSyncService.WithoutExcludedItems(steamItems, new HashSet<string>(["01057800"], StringComparer.Ordinal)).Count,
    "the store id match is exact, not a numeric comparison");
CheckEqual(
    0,
    WishlistSyncService.WithoutExcludedItems(
        steamItems,
        new HashSet<string>(["1057800", "990080"], StringComparer.Ordinal)).Count,
    "every suppressed appid is dropped");

// --- The import shape is untouched (S2) ----------------------------------------------------------
// The acquisition metadata is additive: the columns the imports already write keep their names, and the
// three new ones are nullable, so a Playnite import that knows nothing about them still writes a full row.
var userLibraryColumns = typeof(UserLibrary)
    .GetProperties()
    .Select(property => new ColumnMap(
        property,
        property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>()))
    .ToList();

foreach (var (name, column) in new (string Name, string Column)[]
{
    (nameof(UserLibrary.UserLibraryId), "user_library_id"),
    (nameof(UserLibrary.UserId), "user_id"),
    (nameof(UserLibrary.ItadGameId), "itad_game_id"),
    (nameof(UserLibrary.GameId), "game_id"),
    (nameof(UserLibrary.Store), "store"),
    (nameof(UserLibrary.StoreGameId), "store_game_id"),
    (nameof(UserLibrary.Title), "title"),
    (nameof(UserLibrary.State), "state"),
    (nameof(UserLibrary.IsInstalled), "is_installed"),
    (nameof(UserLibrary.Priority), "priority"),
    (nameof(UserLibrary.AddedAt), "added_at"),
    (nameof(UserLibrary.ImportedAt), "imported_at")
})
{
    var mapped = userLibraryColumns.FirstOrDefault(entry => entry.Property.Name == name);
    CheckEqual<string?>(column, mapped?.Column?.Name, $"user_library.{name} still maps to the column the imports write");
}

var acquisitionProperties = userLibraryColumns
    .Where(entry => entry.Column?.Name is "acquired_store" or "acquisition_type" or "acquisition_date")
    .ToList();
CheckEqual(3, acquisitionProperties.Count, "the acquisition metadata is exactly three core mapped columns");
foreach (var entry in acquisitionProperties)
{
    var type = entry.Property.PropertyType;
    var isNullable = !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    Check(isNullable, $"user_library.{entry.Property.Name} is nullable so imported rows stay valid");
    Check(
        entry.Property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() is null,
        $"user_library.{entry.Property.Name} is not required");
}

// The two source facts are additive columns on the same row: nullable on their own, because every row
// written before them (and every client that does not send them) leaves them NULL on purpose.
var sourceProperties = userLibraryColumns
    .Where(entry => entry.Column?.Name is "acquired_store_type" or "acquisition_provider")
    .ToList();
CheckEqual(2, sourceProperties.Count, "the shop classification and the provider are two mapped columns");
foreach (var entry in sourceProperties)
{
    var type = entry.Property.PropertyType;
    var isNullable = !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    Check(isNullable, $"user_library.{entry.Property.Name} is nullable so previous rows stay valid");
    Check(
        entry.Property.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() is null,
        $"user_library.{entry.Property.Name} is not required");
}

CheckEqual(
    "wishlist_exclusions",
    typeof(WishlistExclusion).GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.TableAttribute>()?.Name,
    "the exclusion table keeps its name");
CheckEqual(
    "store_game_id",
    typeof(WishlistExclusion).GetProperty(nameof(WishlistExclusion.StoreGameId))
        ?.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>()?.Name,
    "the exclusion is keyed by the suppressed store id");

// --- The replay of an already-dismissed submit (S3/L4 idempotency) --------------------------------
// The first submit deletes the wished row, so a retry cannot find it. The local exclusion is the only thing
// that proves this appid was acquired here, and the chosen platform must already hold the game: with both,
// the retry answers exactly like the original success; without them the route stays a 404.
var replayRow = new WishlistAcquisitionExistingRow(77L, "owned", HasAcquisitionMetadata: true);
var replay = WishlistAcquisitionRules.Replay(42L, replayRow, alreadyExcluded: true);
Check(replay is not null, "a repeated submit after the removal replays the original answer");
CheckEqual(WishlistAcquisitionOutcomes.AlreadyOwned, replay?.Outcome, "the replay reports already_owned");
CheckEqual(77L, replay?.UserLibraryId, "the replay reports the same row as the first submit");
CheckEqual(42L, replay?.GameId, "the replay reports the same canonical game");
Check(replay?.Dismissed == true, "the replay reports the removal that already happened");
Check(
    WishlistAcquisitionRules.Replay(42L, new WishlistAcquisitionExistingRow(78L, "subscription", true), true) is not null,
    "a subscription row answers the replay too");
Check(
    WishlistAcquisitionRules.Replay(42L, null, alreadyExcluded: true) is null,
    "no owned row on the chosen platform means no replay: nothing is fabricated");
Check(
    WishlistAcquisitionRules.Replay(42L, replayRow, alreadyExcluded: false) is null,
    "an appid that was never dismissed here cannot be acquired through the replay");

// --- The per-user gate (S3, sync/acquisition serialization) ---------------------------------------
// The key is PostgreSQL's (class, user) pair, so exactly one key exists per user and two unrelated concerns
// never share one. The class is pinned here because changing it would silently stop the sync and the
// acquisition from serializing.
CheckEqual(0x574C01, WishlistUserLock.LockClass, "the per-user wishlist gate class is unchanged");
Check(
    WishlistUserLock.LockClass != unchecked((int)(0x574C00000000L >> 32)),
    "the wishlist gate is not the wishlist-category lock class");

// --- Schema parity (S2) --------------------------------------------------------------------------
// The manual migration and SQL/schema.sql have to describe the same shape, and the C# side has to agree with
// both: a column that exists in only one of them fails on the server, not here. The files are read from the
// repository (searched upwards from the binary), never trusted to a constant.
var schemaFile = FindRepositoryFile(Path.Combine("SQL", "schema.sql"));
var migrationFile = FindRepositoryFile(Path.Combine("SQL", "migrations", "2026-10-18_wishlist_library_acquisition.sql"));
var sourceMigrationFile = FindRepositoryFile(Path.Combine("SQL", "migrations", "2026-10-19_wishlist_acquisition_source.sql"));
var invariantFile = FindRepositoryFile(Path.Combine("SQL", "checks", "wishlist-acquisition.sql"));
Check(schemaFile is not null, "SQL/schema.sql is reachable from the check");
Check(migrationFile is not null, "the dated migration is reachable from the check");
Check(sourceMigrationFile is not null, "the source-metadata migration is reachable from the check");
Check(invariantFile is not null, "the read-only invariant file is reachable from the check");

if (schemaFile is not null && migrationFile is not null)
{
    var schema = File.ReadAllText(schemaFile);
    var migration = File.ReadAllText(migrationFile);
    var userLibrary = TableBlock(schema, "user_library");
    var exclusions = TableBlock(schema, "wishlist_exclusions");

    Check(userLibrary is not null, "schema.sql still declares user_library");
    Check(exclusions is not null, "schema.sql declares wishlist_exclusions");

    foreach (var column in new[] { "acquired_store VARCHAR(32)", "acquisition_type VARCHAR(16)", "acquisition_date DATE" })
    {
        var name = column.Split(' ')[0];
        Check(userLibrary?.Contains(column, StringComparison.Ordinal) == true,
            $"schema.sql declares user_library.{column}");
        Check(migration.Contains($"ADD COLUMN IF NOT EXISTS {name} ", StringComparison.Ordinal),
            $"the migration adds user_library.{name}");
    }

    // The database CHECK and the C# vocabulary are the same five values, or a valid request would be a 500.
    var vocabulary = $"IN ({string.Join(", ", WishlistAcquisitionTypes.All.Select(type => $"'{type}'"))})";
    Check(userLibrary?.Contains(vocabulary, StringComparison.Ordinal) == true,
        "the acquisition_type CHECK lists exactly the C# vocabulary");
    Check(migration.Contains(vocabulary, StringComparison.Ordinal),
        "the migration's acquisition_type CHECK lists exactly the C# vocabulary");

    Check(exclusions?.Contains("UNIQUE (user_id, store, store_game_id)", StringComparison.Ordinal) == true,
        "one exclusion per (user, store, store_game_id)");
    // The unique constraint's index already leads with user_id, so a dedicated per-user index would only add
    // write cost. Neither file may reintroduce it (they have to agree, or a fresh install and a migrated
    // database end up with different indexes).
    Check(schema.Contains("idx_wishlist_exclusions_user", StringComparison.Ordinal) == false,
        "schema.sql does not duplicate the unique index with a per-user index");
    Check(migration.Contains("idx_wishlist_exclusions_user", StringComparison.Ordinal) == false,
        "the migration does not duplicate the unique index with a per-user index");
    Check(migration.Contains("CREATE TABLE IF NOT EXISTS public.wishlist_exclusions", StringComparison.Ordinal),
        "the migration creates the exclusion table");

    // Every mapped column of the entity exists in the table it writes to.
    foreach (var property in typeof(WishlistExclusion).GetProperties())
    {
        var name = property.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>()?.Name;
        if (name is null) continue;
        Check(exclusions?.Contains(name, StringComparison.Ordinal) == true,
            $"wishlist_exclusions.{name} exists in schema.sql");
    }

    Check(migration.Contains("wishlist_exclusions", StringComparison.Ordinal),
        "the migration mentions the exclusion table it creates");
}

// The source facts live in their own dated migration: the 2026-10-18 one may already be applied somewhere,
// so rewriting it would never reach those databases.
if (schemaFile is not null && migrationFile is not null && sourceMigrationFile is not null)
{
    var schema = File.ReadAllText(schemaFile);
    var migration = File.ReadAllText(migrationFile);
    var sourceMigration = File.ReadAllText(sourceMigrationFile);
    var userLibrary = TableBlock(schema, "user_library");

    foreach (var column in new[] { "acquired_store_type VARCHAR(16)", "acquisition_provider VARCHAR(16)" })
    {
        var name = column.Split(' ')[0];
        Check(userLibrary?.Contains(column, StringComparison.Ordinal) == true,
            $"schema.sql declares user_library.{column}");
        Check(sourceMigration.Contains($"ADD COLUMN IF NOT EXISTS {name} ", StringComparison.Ordinal),
            $"the source migration adds user_library.{name}");
    }

    var storeTypeVocabulary =
        $"IN ({string.Join(", ", WishlistAcquiredStoreTypes.All.Select(value => $"'{value}'"))})";
    var providerVocabulary =
        $"IN ({string.Join(", ", WishlistAcquisitionProviders.All.Select(value => $"'{value}'"))})";
    Check(userLibrary?.Contains(storeTypeVocabulary, StringComparison.Ordinal) == true,
        "the acquired_store_type CHECK lists exactly the C# vocabulary");
    Check(sourceMigration.Contains(storeTypeVocabulary, StringComparison.Ordinal),
        "the source migration's acquired_store_type CHECK lists exactly the C# vocabulary");
    Check(userLibrary?.Contains(providerVocabulary, StringComparison.Ordinal) == true,
        "the acquisition_provider CHECK lists exactly the C# vocabulary");
    Check(sourceMigration.Contains(providerVocabulary, StringComparison.Ordinal),
        "the source migration's acquisition_provider CHECK lists exactly the C# vocabulary");

    // Both facts describe the acquisition's shop, so they cannot exist without it (the same unit rule as
    // ck_user_library_acquisition_metadata, a separate constraint so the earlier one is not rewritten).
    Check(userLibrary?.Contains("ck_user_library_acquisition_source", StringComparison.Ordinal) == true,
        "schema.sql declares the source-without-acquisition guard");
    Check(sourceMigration.Contains("ck_user_library_acquisition_source", StringComparison.Ordinal),
        "the source migration declares the source-without-acquisition guard");

    // A new file, never a rewrite of the applied one.
    Check(!migration.Contains("acquired_store_type", StringComparison.Ordinal),
        "the 2026-10-18 migration is not rewritten with the classification column");
    Check(!migration.Contains("acquisition_provider", StringComparison.Ordinal),
        "the 2026-10-18 migration is not rewritten with the provider column");
    Check(sourceMigration.Contains("IF NOT EXISTS", StringComparison.Ordinal),
        "the source migration is idempotent");
    Check(sourceMigration.Contains("FROM pg_constraint WHERE conname = 'ck_user_library_acquired_store_type'", StringComparison.Ordinal),
        "the source migration guards every CHECK it adds, because ADD CONSTRAINT has no IF NOT EXISTS");
}

// The read-only invariants have to cover the new vocabularies, or a database with a drifted CHECK looks clean.
if (invariantFile is not null)
{
    var invariants = File.ReadAllText(invariantFile);
    Check(invariants.Contains("NOT IN ('official', 'keyshop', 'unknown')", StringComparison.Ordinal),
        "the invariants reject a classification outside the vocabulary");
    Check(invariants.Contains("NOT IN ('itad', 'ggdeals', 'direct', 'other')", StringComparison.Ordinal),
        "the invariants reject a provider outside the vocabulary");
    Check(invariants.Contains("acquired_store_type IS NOT NULL", StringComparison.Ordinal),
        "the invariants flag a classification stored without its acquisition");
    Check(invariants.Contains("acquisition_provider IS NOT NULL", StringComparison.Ordinal),
        "the invariants flag a provider stored without its acquisition");
}

// --- Direct acquisition without a snapshot or mapping (S1) -----------------------------------------
// A wished appid can legitimately have no steam_games row and no claimed mapping (the list sync failed to
// fetch details, or the price pass never ran). Identity still comes from the exact (steam, appid) — never a
// title — so the acquisition must work with the wished row's own name instead of failing.
CheckEqual(
    "Steam snapshot name",
    WishlistAcquisitionRules.ResolveAcquisitionTitle(" Steam snapshot name ", "Nombre localizado", "1057800"),
    "the snapshot name wins as display title");
CheckEqual(
    "Caballeros Floppy",
    WishlistAcquisitionRules.ResolveAcquisitionTitle(null, "Caballeros Floppy", "1057800"),
    "the wished title is used when Steam has no snapshot");
CheckEqual(
    "Caballeros Floppy",
    WishlistAcquisitionRules.ResolveAcquisitionTitle("  ", " Caballeros Floppy ", "1057800"),
    "a blank snapshot name falls back to the wished title");
Check(
    WishlistAcquisitionRules.ResolveAcquisitionTitle(null, "1057800", "1057800") is null,
    "the appid placeholder is not a name and must not become a games.title");
Check(
    WishlistAcquisitionRules.ResolveAcquisitionTitle(null, null, "1057800") is null,
    "no name at all means no identity creation");
CheckEqual(
    "1057800 Deluxe",
    WishlistAcquisitionRules.ResolveAcquisitionTitle(null, "1057800 Deluxe", "1057800"),
    "a name that merely contains the appid is still a real name");

// --- Backfill never overwrites a merge (S5) -------------------------------------------------------
// The canonical link is written only when it is still null: an existing value belongs to the merge writer.
Check(WishlistAcquisitionRules.ShouldBackfillCanonicalId(null), "a null canonical link is filled");
Check(!WishlistAcquisitionRules.ShouldBackfillCanonicalId(42L), "an existing canonical link is never overwritten");
Check(!WishlistAcquisitionRules.ShouldBackfillCanonicalId(0L), "a zero id is a value, not a missing one");

// --- The price-refresh title write cannot fight an acquisition (S3) --------------------------------
// Targeted instead of tracked-and-saved, and scoped to wished rows so it can never rename the owned row the
// acquisition just wrote for the same appid.
var titleScope = WishlistSyncService.TitleUpdateScope("1057800");
CheckEqual("steam", titleScope.Store, "the title write stays inside the Steam wishlist");
CheckEqual("wished", titleScope.State, "the title write only touches wished rows");
Check(
    titleScope.State != LibraryStates.Owned && titleScope.State != LibraryStates.Subscription,
    "the title write can never rename an acquired owned/subscription row");
CheckEqual("1057800", titleScope.StoreGameId, "the title write is scoped to the refreshed appid");

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    throw new Exception($"{failures.Count} wishlist acquisition check(s) failed");
}

Console.WriteLine("PASS: all wishlist acquisition checks");

// The repository is located by walking up from the binary, so the check works from any working directory.
static string? FindRepositoryFile(string relativePath)
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, relativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    return null;
}

// One CREATE TABLE block, up to its closing parenthesis line.
static string? TableBlock(string schema, string table)
{
    var start = schema.IndexOf($"CREATE TABLE {table} (", StringComparison.Ordinal);
    if (start < 0)
    {
        return null;
    }

    var end = schema.IndexOf("\n);", start, StringComparison.Ordinal);
    return end < 0 ? schema[start..] : schema[start..end];
}

internal sealed record ColumnMap(
    System.Reflection.PropertyInfo Property,
    System.ComponentModel.DataAnnotations.Schema.ColumnAttribute? Column);
