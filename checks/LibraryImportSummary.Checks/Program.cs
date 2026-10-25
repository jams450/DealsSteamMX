// Deterministic checks for the shared library-import summary (spec S2/S3/S5). Run with:
//   dotnet run --project checks/LibraryImportSummary.Checks
//
// Both import endpoints (POST /api/library/import and POST /api/library/provider-export) share one
// transaction and one loop, so the report they return has to be decided by one pure rule set. That is what
// this suite pins without a database: which stored-vs-incoming difference counts as a real modification,
// that imported_at is not one of them, that a match without a real change still counts as legacy `updated`,
// that a duplicate payload key is counted once, that the added list is complete up to 50 rows and empty (not
// partial) from 51, that nothing is ever reported as deleted, and that the extra fields do not disturb the
// legacy response shape or its JSON names.
//
// What is NOT covered here, and is named so it cannot be mistaken for verified: the transaction, the actual
// insert/update counts and the identity resolver against PostgreSQL. Those need a running database; this
// file covers the decision surface the loop applies to every accepted row.
//
// A thrown Exception or a non-zero exit marks the suite failed; every case is collected first so one broken
// rule does not hide the others.

using System.Text.Json;
using Deals.API.Models.Library;
using Deals.BusinessLogic.Models.Library;

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

// --- The classifier only sees the four business columns (S3) -----------------------------------------
// imported_at is rewritten by every pass; if it were part of the state every matched row would report as
// modified. The property set is pinned so a later "just add ImportedAt" cannot slip through.

CheckEqual(
    "AddedAt, GameId, IsInstalled, Title",
    string.Join(", ", typeof(LibraryImportRowState).GetProperties()
        .Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal)),
    "the change state is exactly the four columns an import writes, so imported_at can never be a change");

var storedDate = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc);
var otherDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
var stored = new LibraryImportRowState("Floppy Knights", true, storedDate, 42);

// A reimport that brings back exactly what is stored is not a modification, even though the loop writes
// imported_at = now and assigns the same values again.
Check(!LibraryImportChanges.IsRealChange(stored, stored), "an identical row is not modified");

// Each business column on its own is a real change.
Check(
    LibraryImportChanges.IsRealChange(stored, stored with { Title = "Floppy Knights Deluxe" }),
    "a changed title is a real modification");
Check(
    LibraryImportChanges.IsRealChange(stored, stored with { IsInstalled = false }),
    "a flipped is_installed is a real modification");
Check(
    LibraryImportChanges.IsRealChange(stored, stored with { IsInstalled = null }),
    "a stored null is_installed filled in is a real modification");
Check(
    LibraryImportChanges.IsRealChange(stored, stored with { AddedAt = otherDate }),
    "a changed added_at is a real modification");
Check(
    LibraryImportChanges.IsRealChange(stored, stored with { GameId = 43 }),
    "a new canonical game_id is a real modification");
Check(
    LibraryImportChanges.IsRealChange(stored with { GameId = null }, stored),
    "a first canonical identity on a row that had none is a real modification");

// Identity is compared as stored: a canonical id reassigned by the resolver is a change, not noise.
Check(!LibraryImportChanges.IsRealChange(stored, stored with { GameId = 42 }), "the same game_id is not a change");

// --- The merge the loop writes is what gets classified (Playnite and Heroic) --------------------------
// The Playnite export always carries Added, so its date wins, exactly as before this feature.
var mergedPlaynite = LibraryImportChanges.Merge(stored, "Floppy Knights", true, otherDate, 42);
CheckEqual(otherDate, mergedPlaynite.AddedAt, "a Playnite reimport writes its own added date");
Check(LibraryImportChanges.IsRealChange(stored, mergedPlaynite), "a Playnite date change is reported as modified");

// The Heroic envelope has no date at all: the stored one must survive and must not read as a change.
var mergedHeroic = LibraryImportChanges.Merge(stored, "Floppy Knights", true, incomingAddedAt: null, 42);
CheckEqual(storedDate, mergedHeroic.AddedAt, "a Heroic reimport keeps the stored added date");
Check(!LibraryImportChanges.IsRealChange(stored, mergedHeroic), "a Heroic reimport of an unchanged row is not modified");

// A row imported by Heroic (no date) and reimported by Heroic stays without a date and stays unmodified.
var undated = new LibraryImportRowState("Floppy Knights", true, null, 42);
var undatedMerged = LibraryImportChanges.Merge(undated, "Floppy Knights", true, incomingAddedAt: null, 42);
CheckEqual<DateTime?>(null, undatedMerged.AddedAt, "an undated row gains no invented date");
Check(!LibraryImportChanges.IsRealChange(undated, undatedMerged), "an undated row reimported unchanged is not modified");

// A Playnite import does fill the date a Heroic import left empty: that is a real change.
var undatedFilled = LibraryImportChanges.Merge(undated, "Floppy Knights", true, otherDate, 42);
Check(LibraryImportChanges.IsRealChange(undated, undatedFilled), "a date arriving where none was stored is a change");

// The loop writes the merged state, so the classified state and the written state cannot drift.
CheckEqual(
    mergedHeroic,
    new LibraryImportRowState(mergedHeroic.Title, mergedHeroic.IsInstalled, mergedHeroic.AddedAt, mergedHeroic.GameId),
    "the merged state is exactly what the loop assigns to the row");

// --- The accumulator: legacy `updated` keeps counting every match (S3) -------------------------------

var summary = new LibraryImportSummary();
summary.CountUpdated(stored, stored);
CheckEqual(1, summary.Updated, "a matched row still counts as legacy updated");
CheckEqual(0, summary.Modified, "a matched row with no real change is not modified");
summary.CountUpdated(stored, mergedHeroic);
CheckEqual(2, summary.Updated, "a Heroic match still counts as legacy updated");
CheckEqual(0, summary.Modified, "a Heroic no-op match adds no modification");
summary.CountUpdated(stored, stored with { Title = "Floppy Knights Deluxe" });
CheckEqual(3, summary.Updated, "every match is counted in updated");
CheckEqual(1, summary.Modified, "only the match that really changed is counted in modified");

// --- The added list is server bounded: complete to 50, empty from 51 (S3/S5) -------------------------

LibraryImportSummary WithAdded(int count, string platform = StoreKeys.Epic)
{
    var tally = new LibraryImportSummary();
    for (var index = 0; index < count; index++)
    {
        tally.CountAdded($"Juego {index + 1}", platform);
    }

    return tally;
}

CheckEqual(0, WithAdded(0).AddedItems.Count, "zero added rows report an empty list");
CheckEqual(0, WithAdded(0).Imported, "zero added rows report zero imported");

var one = WithAdded(1);
CheckEqual(1, one.Imported, "one added row is counted");
CheckEqual("Juego 1|epic", $"{one.AddedItems[0].Title}|{one.AddedItems[0].Platform}", "the item carries title and platform");
CheckEqual(
    StoreKeys.Gog,
    WithAdded(1, StoreKeys.Gog).AddedItems[0].Platform,
    "the platform is the store key of the accepted row, not an inferred OS");

var fifty = WithAdded(50);
CheckEqual(50, fifty.Imported, "50 added rows are counted");
CheckEqual(50, fifty.AddedItems.Count, "50 added rows are all listed (50 is inclusive)");
CheckEqual("Juego 1|Juego 50", $"{fifty.AddedItems[0].Title}|{fifty.AddedItems[^1].Title}", "the 50 listed rows are in payload order");

var fiftyOne = WithAdded(51);
CheckEqual(51, fiftyOne.Imported, "51 added rows are all counted");
CheckEqual(0, fiftyOne.AddedItems.Count, "51 added rows list nothing instead of a misleading partial preview");

CheckEqual(
    50,
    WithAdded(LibraryImportSummary.MaxListedItems).AddedItems.Count,
    "the boundary constant is 50, the number every other layer expects");

// --- Nothing is ever deleted (S2) -------------------------------------------------------------------

var noPrune = new LibraryImportSummary();
noPrune.CountAdded("Floppy Knights", StoreKeys.Epic);
CheckEqual(0, noPrune.Deleted, "the summary never reports a deleted row");
CheckEqual(0, noPrune.DeletedItems.Count, "the deleted list is always empty");
CheckEqual(0, WithAdded(51).DeletedItems.Count, "the deleted list stays empty past the added boundary");

// --- A duplicate payload key is counted once (S3) ---------------------------------------------------

var dedupe = new LibraryImportSummary();
Check(dedupe.TryBegin("epic", "Kinglet", LibraryStates.Owned), "the first occurrence of a key is accepted");
Check(!dedupe.TryBegin("epic", "Kinglet", LibraryStates.Owned), "a duplicate key is skipped, so it is counted once");
Check(
    dedupe.TryBegin("epic", "Kinglet", LibraryStates.Subscription),
    "the state is part of the row key, exactly as user_library is keyed, so another state is another row");
Check(dedupe.TryBegin("epic", "Otro", LibraryStates.Owned), "a different id is a different row");
Check(dedupe.TryBegin("gog", "Kinglet", LibraryStates.Owned), "a different store is a different row");

// --- The legacy response shape and the new field names (S3/L2) ---------------------------------------

var byStore = new Dictionary<string, int>(StringComparer.Ordinal) { [StoreKeys.Epic] = 1 };

// An old consumer that builds or reads the five-field shape keeps working: the extras are optional.
var legacy = new LibraryImportResponse(1, 0, 0, 0, byStore);
CheckEqual(0, legacy.Modified, "a legacy-shaped response reports no modifications instead of inventing them");
CheckEqual(0, legacy.Deleted, "a legacy-shaped response reports no deletions");
Check(legacy.AddedItems is null, "a legacy-shaped response reports no item list rather than a fabricated one");

var report = LibraryImportResponse.From(noPrune, unsupportedSource: 0, byStore);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Check(json.Contains("\"imported\":1", StringComparison.Ordinal), "the legacy imported field keeps its name");
Check(json.Contains("\"unsupportedSource\":0", StringComparison.Ordinal), "the legacy unsupportedSource field keeps its name");
Check(json.Contains("\"modified\":0", StringComparison.Ordinal), "the new field is serialized as modified");
Check(json.Contains("\"deleted\":0", StringComparison.Ordinal), "the new field is serialized as deleted");
Check(
    json.Contains("\"addedItems\":[{\"title\":\"Floppy Knights\",\"platform\":\"epic\"}]", StringComparison.Ordinal),
    "the added list is serialized as {title, platform}");
Check(json.Contains("\"deletedItems\":[]", StringComparison.Ordinal), "the deleted list is serialized as an empty array");

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("PASS: library import summary classifier, accumulator, bounds and response contract");
return 0;
