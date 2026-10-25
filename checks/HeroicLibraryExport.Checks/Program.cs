// Deterministic checks for the Heroic library export gate (spec S1/S3/S4/S5). Run with:
//   dotnet run --project checks/HeroicLibraryExport.Checks
//
// The endpoint /api/library/provider-export shares the Playnite import's transaction, upsert and identity
// resolver, so the only thing this suite can and must pin without a database is the decision surface that
// runs *before* the transaction opens: the envelope, the store vocabulary, the field bounds, the boolean,
// the numeric GOG ids and the "one bad row rejects the whole payload" rule. It also pins the two rules the
// shared loop applies to every accepted row: the state is forced to owned and an absent AddedAt never
// erases the one already stored.
//
// What is NOT covered here, and is named so it cannot be mistaken for verified: the transaction itself, the
// insert/update counts, identity resolution and the AddedAt preservation *against PostgreSQL*. Those need a
// running database and are the evidence of the build plus a live import, not of this file.
//
// A thrown Exception or a non-zero exit marks the suite failed; every case is collected first so one broken
// rule does not hide the others.

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

// --- The frozen envelope (L4/L5) --------------------------------------------------------------------

CheckEqual("dealext-library", HeroicLibraryExportLimits.Format, "the envelope format is the frozen one");
CheckEqual(1, HeroicLibraryExportLimits.Version, "the envelope version is 1");
CheckEqual("heroic", HeroicLibraryExportLimits.Source, "the envelope source is heroic");
// Same cap as POST /api/library/import: the gate is not a widening of the frozen Playnite input.
CheckEqual(10_000, HeroicLibraryExportLimits.MaxEntries, "the entry cap matches the Playnite import cap");
CheckEqual(256, HeroicLibraryExportLimits.MaxTitleLength, "the title cap matches user_library.title");
CheckEqual(64, HeroicLibraryExportLimits.MaxStoreGameIdLength, "the id cap matches user_library.store_game_id");

CheckEqual(
    "epic, gog",
    string.Join(", ", HeroicLibraryExportLimits.Stores),
    "only the two stores Heroic can export are supported, in contract order");
Check(HeroicLibraryExportLimits.IsSupportedStore(HeroicLibraryExportLimits.Epic), "epic is a supported store");
Check(HeroicLibraryExportLimits.IsSupportedStore(HeroicLibraryExportLimits.Gog), "gog is a supported store");
Check(!HeroicLibraryExportLimits.IsSupportedStore(StoreKeys.Steam), "steam is not a Heroic export store");
Check(!HeroicLibraryExportLimits.IsSupportedStore(StoreKeys.Xbox), "xbox is not a Heroic export store");
Check(!HeroicLibraryExportLimits.IsSupportedStore(null), "a missing store is never supported");

// --- A valid export is accepted and its rows carry the forced state ----------------------------------

const string ExportedAt = "2026-10-02T18:04:05Z";

static HeroicLibraryEntryInput? Row(string? store, string? id, string? title, bool? installed) =>
    new(store, id, title, installed);

HeroicLibraryValidationResult Validate(
    IReadOnlyList<HeroicLibraryEntryInput?>? games,
    string? format = HeroicLibraryExportLimits.Format,
    int? version = HeroicLibraryExportLimits.Version,
    string? source = HeroicLibraryExportLimits.Source,
    string? exportedAt = ExportedAt) =>
    HeroicLibraryExportRules.Validate(format, version, source, exportedAt, games);

var accepted = Validate([
    Row("epic", "Kinglet", "Floppy Knights", true),
    Row("gog", "1423049311", "Cyberpunk 2077", false)
]);

CheckEqual(2, accepted.Rows.Count, "both rows of a valid export are accepted");
CheckEqual(
    LibraryStates.Owned,
    accepted.Rows[0].State,
    "the state is forced to owned: the envelope never carries ownership");
CheckEqual(true, accepted.Rows[0].IsInstalled, "the exported installed flag is preserved");
CheckEqual("Kinglet", accepted.Rows[0].StoreGameId, "the exact Epic app_name is preserved as the store id");
CheckEqual("1423049311", accepted.Rows[1].StoreGameId, "the exact GOG product id is preserved as the store id");
CheckEqual(1, accepted.ByStore["epic"], "the report counts the epic rows");
CheckEqual(1, accepted.ByStore["gog"], "the report counts the gog rows");

// The store is a closed vocabulary key, not an identity: it is trimmed and lowercased so " GOG " cannot
// become a second store in user_library.store. The title is display text and is trimmed too. The store id is
// NOT: see the rejection cases below.
var normalized = Validate([Row(" GOG ", "1423049311", " Cyberpunk 2077 ", true)]);
CheckEqual("gog", normalized.Rows[0].Store, "the store is trimmed and lowercased before it is written");
CheckEqual("1423049311", normalized.Rows[0].StoreGameId, "the store id is written exactly as it arrived");
CheckEqual("Cyberpunk 2077", normalized.Rows[0].Title, "the title is trimmed before it is written");

// --- Envelope rejection ------------------------------------------------------------------------------

CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], format: "playnite"), "an unknown format is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], format: null), "a missing format is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], format: "DEALEXT-LIBRARY"), "the format is case-sensitive");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], version: 2), "version 2 is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], version: null), "a missing version is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], source: "playnite"), "a Playnite envelope cannot pass the Heroic gate");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], source: null), "a missing source is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: null), "a missing exportedAt is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: ""), "a blank exportedAt is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: "ayer"), "a non-ISO exportedAt is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: "2026-02-30T00:00:00Z"), "an impossible calendar day is rejected");
// A timestamp without a zone, a bare date, a space separator and a non-ISO local shape all used to be
// accepted by the loose DateTimeOffset parse; each one is refused now, because "assume UTC" decides for the
// exporter which instant it meant.
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: "2026-10-02T18:04:05"), "an exportedAt without a zone is rejected instead of assumed UTC");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: "2026-10-02"), "a date-only exportedAt is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: "2026-10-02 18:04:05Z"), "an exportedAt with a space separator is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: "02/10/2026 18:04:05"), "an exportedAt in a local date shape is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: "2026-10-02T18:04:05.1234567Z"), "an exportedAt with sub-millisecond precision is rejected, as the browser gate does");
CheckEqual(
    new DateTime(2026, 10, 2, 18, 4, 5, 123, DateTimeKind.Utc),
    HeroicLibraryExportRules.ParseExportedAt("2026-10-02T18:04:05.123Z"),
    "a millisecond fraction is kept");
CheckEqual(
    new DateTime(2026, 10, 2, 18, 4, 0, DateTimeKind.Utc),
    HeroicLibraryExportRules.ParseExportedAt("2026-10-02T18:04+00:00"),
    "seconds are optional and a +00:00 offset is the same instant as Z");
// The envelope promises UTC. A local-offset timestamp would be stored shifted, so it is refused instead of
// silently converted.
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true)], exportedAt: "2026-10-02T18:04:05+02:00"), "a non-UTC exportedAt is rejected");
CheckEqual(
    new DateTime(2026, 10, 2, 18, 4, 5, DateTimeKind.Utc),
    HeroicLibraryExportRules.ParseExportedAt(ExportedAt),
    "a UTC exportedAt is parsed to a UTC instant");

CheckThrows<ArgumentException>(() => Validate(null), "a missing games array is rejected");
CheckThrows<ArgumentException>(() => Validate([]), "an empty games array is rejected");

var atCap = Enumerable
    .Range(0, HeroicLibraryExportLimits.MaxEntries)
    .Select(index => Row("epic", $"app{index}", $"Juego {index}", true))
    .ToList();
CheckEqual(
    HeroicLibraryExportLimits.MaxEntries,
    Validate(atCap).Rows.Count,
    "an export exactly at the cap is accepted");
var overCap = atCap.Append(Row("epic", "appOverflow", "Juego de más", true)).ToList();
CheckThrows<ArgumentException>(() => Validate(overCap), "an export over the cap is rejected before any row is read");

// --- Row rejection -----------------------------------------------------------------------------------

CheckThrows<ArgumentException>(() => Validate([Row(null, "Kinglet", "Floppy Knights", true)]), "a row without a store is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("", "Kinglet", "Floppy Knights", true)]), "a blank store is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("steam", "730", "Counter-Strike 2", true)]), "a store outside epic|gog is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("amazon", "amzn1", "Juego", true)]), "an unlisted canonical store is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("Epic; DROP", "Kinglet", "Floppy Knights", true)]), "a junk store value is rejected");

CheckThrows<ArgumentException>(() => Validate([Row("epic", null, "Floppy Knights", true)]), "a missing storeGameId is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "   ", "Floppy Knights", true)]), "a blank storeGameId is rejected");
// The store id is identity: it is never silently trimmed. A padded id would otherwise be written as a key the
// file never contained, and an Epic app_name is opaque, so the gate cannot tell " Kinglet" from a real typo.
CheckThrows<ArgumentException>(() => Validate([Row("epic", " Kinglet", "Floppy Knights", true)]), "a storeGameId with a leading space is rejected, not trimmed");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet ", "Floppy Knights", true)]), "a storeGameId with a trailing space is rejected, not trimmed");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "King\tlet", "Floppy Knights", true)]), "a storeGameId with a tab is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "King\u0000let", "Floppy Knights", true)]), "a storeGameId with a NUL byte is rejected (PostgreSQL cannot store it)");
CheckEqual(
    "Kinglet Two",
    Validate([Row("epic", "Kinglet Two", "Floppy Knights", true)]).Rows[0].StoreGameId,
    "an inner space is part of the id and is kept");
CheckEqual(
    new string('9', HeroicLibraryExportLimits.MaxStoreGameIdLength),
    Validate([Row("gog", new string('9', HeroicLibraryExportLimits.MaxStoreGameIdLength), "Juego", true)])
        .Rows[0].StoreGameId,
    "a numeric GOG id exactly at the column width is accepted (the exporter caps digits, not the wire)");
CheckThrows<ArgumentException>(
    () => Validate([Row("gog", new string('9', HeroicLibraryExportLimits.MaxStoreGameIdLength + 1), "Juego", true)]),
    "a numeric GOG id past the column width is rejected");
CheckThrows<ArgumentException>(
    () => Validate([Row("epic", new string('a', HeroicLibraryExportLimits.MaxStoreGameIdLength + 1), "Floppy Knights", true)]),
    "an oversized storeGameId is rejected instead of aborting the transaction with 22001");
CheckEqual(
    HeroicLibraryExportLimits.MaxStoreGameIdLength,
    Validate([Row("epic", new string('a', HeroicLibraryExportLimits.MaxStoreGameIdLength), "Floppy Knights", true)])
        .Rows[0].StoreGameId.Length,
    "a storeGameId exactly at the column width is accepted");

CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", null, true)]), "a missing title is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "  ", true)]), "a blank title is rejected");
CheckThrows<ArgumentException>(
    () => Validate([Row("epic", "Kinglet", new string('t', HeroicLibraryExportLimits.MaxTitleLength + 1), true)]),
    "an oversized title is rejected instead of aborting the transaction with 22001");

// `isInstalled` is required and a boolean: a null or a string must not become "false" by accident.
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", null)]), "a missing isInstalled is rejected");
CheckEqual(false, Validate([Row("epic", "Kinglet", "Floppy Knights", false)]).Rows[0].IsInstalled, "an explicit false is preserved");

// GOG identifies a game by a numeric product id, so a non-numeric value cannot be a GOG row.
CheckThrows<ArgumentException>(() => Validate([Row("gog", "cyberpunk-2077", "Cyberpunk 2077", true)]), "a non-numeric GOG id is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("gog", "14230 49311", "Cyberpunk 2077", true)]), "a GOG id with an inner space is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("gog", "1e9", "Juego", true)]), "a GOG id in scientific notation is rejected");
CheckThrows<ArgumentException>(() => Validate([Row("gog", "-1423", "Juego", true)]), "a signed GOG id is rejected");
// Epic app_names are opaque strings, so a non-numeric Epic id is the normal case and stays accepted.
CheckEqual(
    "a1b2c3d4e5f60718293a4b5c6d7e8f90",
    Validate([Row("epic", "a1b2c3d4e5f60718293a4b5c6d7e8f90", "Floppy Knights", true)]).Rows[0].StoreGameId,
    "an opaque Epic app_name is accepted as the store id");

CheckThrows<ArgumentException>(() => Validate([null]), "a null entry is rejected instead of being skipped");
CheckThrows<ArgumentException>(() => Validate([Row("epic", "Kinglet", "Floppy Knights", true), null]), "a null entry after a valid row rejects the payload");

// --- The whole payload is validated before anything is written ---------------------------------------
// One bad row after a good one must reject the payload: the shared import loop writes inside a single
// transaction, so a row rejected mid-loop would either lose the accepted rows or commit a partial import.
CheckThrows<ArgumentException>(
    () => Validate([
        Row("epic", "Kinglet", "Floppy Knights", true),
        Row("gog", "not-a-number", "Cyberpunk 2077", true)
    ]),
    "a bad row after a good row rejects the whole payload");

CheckThrows<ArgumentException>(
    () => Validate([
        Row("epic", "Kinglet", "Floppy Knights", true),
        Row("steam", "730", "Counter-Strike 2", true)
    ]),
    "an unsupported store after a good row rejects the whole payload, it is never counted and skipped");

CheckThrows<ArgumentException>(
    () => Validate([
        Row("epic", "Kinglet", "Floppy Knights", true),
        Row("epic", null, "Otro juego", true)
    ]),
    "a missing id after a good row rejects the whole payload");

// --- The shared loop's two rules ---------------------------------------------------------------------
// Both live in the controller that owns the transaction; these two helpers are the whole decision.

var stored = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc);
CheckEqual(
    stored,
    LibraryImportDates.Merge(existing: stored, incoming: null),
    "a Heroic reimport keeps the AddedAt already stored, because the envelope carries no date");
CheckEqual(
    new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    LibraryImportDates.Merge(existing: stored, incoming: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
    "a Playnite reimport still writes its own Added, so Playnite semantics are unchanged");
CheckEqual<DateTime?>(
    null,
    LibraryImportDates.Merge(existing: null, incoming: null),
    "a first Heroic import stores no AddedAt and invents none");

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("PASS: heroic library export envelope, bounds and shared import rules");
return 0;
