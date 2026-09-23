import assert from "node:assert/strict";
import test from "node:test";
import {
  CONSOLE_IMPORT_MAX_ENTRIES,
  CONSOLE_IMPORT_PLATFORM_OTHER,
  buildConsoleImportDecision,
  consoleImportPlatformGroups,
  initialConsoleImportDraft,
  isConsoleImportDraftReady,
  normalizeConsoleImportCommit,
  normalizeConsoleImportPreview,
  parseConsoleImportFile,
  resolveConsoleImportPlatform,
  toConsoleImportEntryPayload,
  type ConsoleImportEntryInput,
  type ConsoleImportPreviewEntry
} from "./console-import.ts";

const SWITCH_ENTRY: ConsoleImportEntryInput = {
  entryId: "console-1",
  name: "Metroid Dread",
  platforms: ["Nintendo Switch"],
  isInstalled: false,
  added: "/Date(1600000000000)/"
};

// --- parseConsoleImportFile ------------------------------------------------------------------------

test("parseConsoleImportFile: separa las filas de consola de las de tienda", () => {
  const parsed = parseConsoleImportFile([
    { GameId: "steam-1", PluginId: "cb91dfc9", Source: "Steam", Name: "Cyberpunk 2077", IsInstalled: true },
    { GameId: "console-1", Source: null, Name: "Metroid Dread", Platforms: ["Nintendo Switch"], IsInstalled: false }
  ]);

  assert.ok(parsed);
  assert.equal(parsed.totalRows, 2);
  assert.equal(parsed.entries.length, 1);
  assert.equal(parsed.entries[0]?.entryId, "console-1");
  assert.deepEqual(parsed.entries[0]?.platforms, ["Nintendo Switch"]);
  assert.deepEqual(parsed.skipped, [
    { entryId: "steam-1", name: "Cyberpunk 2077", source: "Steam", reason: "store_row" }
  ]);
});

test("parseConsoleImportFile: sin Source, `Source: \"\"` también es de consola", () => {
  const parsed = parseConsoleImportFile([{ GameId: "c", Name: "Juego", Source: "   " }]);
  assert.ok(parsed);
  assert.equal(parsed.entries.length, 1);
});

test("parseConsoleImportFile: clasifica cada fila que queda fuera con su motivo", () => {
  const parsed = parseConsoleImportFile([
    { GameId: "c1", Name: "Uno", Platforms: ["Nintendo DS"] },
    { Name: "sin id" },
    { GameId: "c2" },
    { GameId: "c1", Name: "Uno repetido" },
    "no soy un objeto",
    { GameId: "c3", Name: "x".repeat(300) }
  ]);

  assert.ok(parsed);
  assert.equal(parsed.entries.length, 1);
  assert.equal(parsed.entries[0]?.name, "Uno");
  assert.deepEqual(
    parsed.skipped.map((entry) => entry.reason),
    ["missing_id", "missing_name", "duplicate_id", "malformed", "title_too_long"]
  );
});

test("parseConsoleImportFile: acepta Platforms como textos o como objetos con Name", () => {
  const parsed = parseConsoleImportFile([
    { GameId: "c1", Name: "Con textos", Platforms: ["Nintendo Switch", "Nintendo Switch"] },
    { GameId: "c2", Name: "Con objetos", Platforms: [{ Name: "PlayStation 5" }, { name: "PSP" }] }
  ]);

  assert.ok(parsed);
  assert.deepEqual(parsed.entries[0]?.platforms, ["Nintendo Switch"]);
  assert.deepEqual(parsed.entries[1]?.platforms, ["PlayStation 5", "PSP"]);
});

test("parseConsoleImportFile: una fecha ilegible se descarta y se cuenta, no rompe el archivo", () => {
  const parsed = parseConsoleImportFile([
    { GameId: "c1", Name: "Buena", Added: "/Date(1700000000000)/" },
    { GameId: "c2", Name: "Mala", Added: "ayer por la tarde" },
    { GameId: "c3", Name: "ISO", Added: "2021-03-04T05:06:07Z" },
    { GameId: "c4", Name: "Sin fecha" }
  ]);

  assert.ok(parsed);
  assert.equal(parsed.entries.length, 4);
  assert.equal(parsed.droppedDates, 1);
  assert.equal(parsed.entries[1]?.added, null);
  assert.equal(parsed.entries[2]?.added, "2021-03-04T05:06:07Z");
  assert.equal(parsed.entries[3]?.added, null);
});

test("parseConsoleImportFile: solo un booleano literal marca instalado", () => {
  const parsed = parseConsoleImportFile([
    { GameId: "c1", Name: "Uno", IsInstalled: "true" },
    { GameId: "c2", Name: "Dos", IsInstalled: true }
  ]);

  assert.ok(parsed);
  assert.equal(parsed.entries[0]?.isInstalled, null);
  assert.equal(parsed.entries[1]?.isInstalled, true);
});

test("parseConsoleImportFile: una raíz que no es arreglo es null; un arreglo vacío no", () => {
  assert.equal(parseConsoleImportFile({ items: [] }), null);
  assert.equal(parseConsoleImportFile("nope"), null);

  const empty = parseConsoleImportFile([]);
  assert.ok(empty);
  assert.deepEqual(empty.entries, []);
  assert.equal(empty.totalRows, 0);
  assert.equal(empty.exceedsEntryLimit, false);
});

test("parseConsoleImportFile: más de 2000 entradas de consola no se recortan, se señalan", () => {
  const row = (index: number) => ({ GameId: `c${index}`, Name: `Juego ${index}` });

  const atLimit = parseConsoleImportFile(Array.from({ length: CONSOLE_IMPORT_MAX_ENTRIES }, (_, index) => row(index)));
  assert.ok(atLimit);
  assert.equal(atLimit.entries.length, CONSOLE_IMPORT_MAX_ENTRIES);
  assert.equal(atLimit.totalRows, CONSOLE_IMPORT_MAX_ENTRIES);
  assert.equal(atLimit.exceedsEntryLimit, false);

  const overLimit = parseConsoleImportFile(
    Array.from({ length: CONSOLE_IMPORT_MAX_ENTRIES + 1 }, (_, index) => row(index))
  );
  assert.ok(overLimit);
  assert.equal(overLimit.entries.length, CONSOLE_IMPORT_MAX_ENTRIES + 1);
  assert.equal(overLimit.totalRows, CONSOLE_IMPORT_MAX_ENTRIES + 1);
  assert.equal(overLimit.exceedsEntryLimit, true);
});

test("parseConsoleImportFile: las filas de tienda no cuentan para el tope de entradas", () => {
  const storeRows = Array.from({ length: CONSOLE_IMPORT_MAX_ENTRIES + 5 }, (_, index) => ({
    GameId: `s${index}`,
    Source: "Steam",
    Name: `Tienda ${index}`
  }));
  const parsed = parseConsoleImportFile([...storeRows, { GameId: "console-1", Name: "Metroid Dread" }]);

  assert.ok(parsed);
  assert.equal(parsed.entries.length, 1);
  assert.equal(parsed.skipped.length, CONSOLE_IMPORT_MAX_ENTRIES + 5);
  assert.equal(parsed.totalRows, CONSOLE_IMPORT_MAX_ENTRIES + 6);
  assert.equal(parsed.exceedsEntryLimit, false);
});

test("toConsoleImportEntryPayload: manda `source: null` explícito y no inventa campos", () => {
  assert.deepEqual(toConsoleImportEntryPayload(SWITCH_ENTRY), {
    gameId: "console-1",
    source: null,
    name: "Metroid Dread",
    platforms: ["Nintendo Switch"],
    isInstalled: false,
    added: "/Date(1600000000000)/"
  });
});

// --- normalizeConsoleImportPreview -----------------------------------------------------------------

const PREVIEW_ENTRY = {
  index: 0,
  entryId: "console-1",
  name: "Metroid Dread",
  candidates: [{ gameId: 42, title: "Metroid Dread", releaseYear: 2021, imageUrl: "https://x/y.jpg", inLibrary: true }],
  suggestedPlatforms: [{ slug: "switch", displayName: "Nintendo Switch", source: "Nintendo Switch" }],
  needsPlatform: false
};

test("normalizeConsoleImportPreview: parsea candidatos y sugerencias", () => {
  const preview = normalizeConsoleImportPreview({ entries: [PREVIEW_ENTRY] });
  assert.ok(preview);
  assert.equal(preview.entries.length, 1);
  assert.equal(preview.entries[0]?.candidates[0]?.gameId, 42);
  assert.equal(preview.entries[0]?.candidates[0]?.inLibrary, true);
  assert.equal(preview.entries[0]?.suggestedPlatforms[0]?.slug, "switch");
  assert.equal(preview.entries[0]?.needsPlatform, false);
});

test("normalizeConsoleImportPreview: una entrada rota rechaza el reporte entero (no se omite en silencio)", () => {
  assert.equal(normalizeConsoleImportPreview({ entries: [{ ...PREVIEW_ENTRY, entryId: "" }] }), null);
  assert.equal(normalizeConsoleImportPreview({ entries: [{ ...PREVIEW_ENTRY, candidates: [{ gameId: 0 }] }] }), null);
  assert.equal(normalizeConsoleImportPreview({ entries: "nope" }), null);
  assert.equal(normalizeConsoleImportPreview("garbage"), null);
});

test("normalizeConsoleImportPreview: sin sugerencias válidas, la fila pide plataforma", () => {
  const preview = normalizeConsoleImportPreview({
    entries: [{ ...PREVIEW_ENTRY, suggestedPlatforms: [], needsPlatform: true }]
  });
  assert.ok(preview);
  assert.equal(preview.entries[0]?.needsPlatform, true);
  assert.deepEqual(preview.entries[0]?.suggestedPlatforms, []);
});

test("normalizeConsoleImportPreview: una sugerencia que no es slug se descarta y obliga a elegir", () => {
  const preview = normalizeConsoleImportPreview({
    entries: [{ ...PREVIEW_ENTRY, suggestedPlatforms: [{ slug: "Nintendo Switch", displayName: "Switch", source: "Nintendo Switch" }] }]
  });
  assert.ok(preview);
  assert.deepEqual(preview.entries[0]?.suggestedPlatforms, []);
  assert.equal(preview.entries[0]?.needsPlatform, true);
  assert.equal(normalizeConsoleImportPreview({ entries: [{ ...PREVIEW_ENTRY, suggestedPlatforms: [{ slug: "switch" }] }] }), null);
});

test("normalizeConsoleImportPreview: una sugerencia de tienda de PC se descarta", () => {
  const preview = normalizeConsoleImportPreview({
    entries: [{ ...PREVIEW_ENTRY, suggestedPlatforms: [{ slug: "steam", displayName: "Steam", source: "Steam" }] }]
  });
  assert.ok(preview);
  assert.deepEqual(preview.entries[0]?.suggestedPlatforms, []);
  assert.equal(preview.entries[0]?.needsPlatform, true);
});

// --- decisiones ------------------------------------------------------------------------------------

const PREVIEW_ENTRY_TYPED = PREVIEW_ENTRY as unknown as ConsoleImportPreviewEntry;

test("initialConsoleImportDraft: arranca en la sugerencia y en «crear juego nuevo»", () => {
  const draft = initialConsoleImportDraft(PREVIEW_ENTRY_TYPED);
  assert.equal(draft.platform, "switch");
  assert.equal(draft.attachGameId, null);
  assert.equal(isConsoleImportDraftReady(draft), true);
});

test("initialConsoleImportDraft: sin sugerencias la plataforma queda vacía y la fila no está lista", () => {
  const draft = initialConsoleImportDraft({ ...PREVIEW_ENTRY_TYPED, suggestedPlatforms: [], needsPlatform: true });
  assert.equal(draft.platform, "");
  assert.equal(isConsoleImportDraftReady(draft), false);
});

test("resolveConsoleImportPlatform: la marca «Otra plataforma…» no es un slug válido", () => {
  const base = { entryId: "c", name: "Juego", attachGameId: null };
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "" }), null);
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: CONSOLE_IMPORT_PLATFORM_OTHER }), null);
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "  " }), null);
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "Sega Saturn" }), null);
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "sega-saturn" }), "sega-saturn");
});

test("resolveConsoleImportPlatform: las tiendas de PC no son plataformas de consola", () => {
  const base = { entryId: "c", name: "Juego", attachGameId: null };
  // El backend rechaza las ocho llaves de StoreKeys como `ownedPlatform`; ninguna deja la fila lista.
  for (const platform of ["steam", "epic", "gog", "xbox", "amazon", "ubisoft", "humble", "battlenet"]) {
    assert.equal(resolveConsoleImportPlatform({ ...base, platform }), null, platform);
  }
  // Alias reales del export de Playnite y de la biblioteca.
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "Ubisoft Connect" }), null);
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "Battle.net" }), null);
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "Epic Games" }), null);
  // Las consolas de Xbox no son la tienda `xbox`: siguen siendo válidas.
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "xbox-one" }), "xbox-one");
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "xbox360" }), "xbox360");
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "series-s" }), "series-s");
  assert.equal(resolveConsoleImportPlatform({ ...base, platform: "switch" }), "switch");
});

test("isConsoleImportDraftReady: una tienda de PC no deja la fila lista", () => {
  const base = { entryId: "c", name: "Juego", attachGameId: null };
  assert.equal(isConsoleImportDraftReady({ ...base, platform: "steam" }), false);
  assert.equal(isConsoleImportDraftReady({ ...base, platform: "Ubisoft Connect" }), false);
  assert.equal(isConsoleImportDraftReady({ ...base, platform: "switch" }), true);
});

test("buildConsoleImportDecision: exactamente una acción de identidad", () => {
  const base = { entryId: "console-1", name: "Metroid Dread", platform: "switch" };

  const created = buildConsoleImportDecision({ ...base, attachGameId: null }, SWITCH_ENTRY);
  assert.ok(created);
  assert.equal(created.create, true);
  assert.equal(created.attachGameId, null);
  assert.equal(created.ownedPlatform, "switch");
  assert.equal(created.isInstalled, false);
  assert.equal(created.added, "/Date(1600000000000)/");

  const attached = buildConsoleImportDecision({ ...base, attachGameId: 42 }, SWITCH_ENTRY);
  assert.ok(attached);
  assert.equal(attached.create, false);
  assert.equal(attached.attachGameId, 42);

  assert.equal(buildConsoleImportDecision({ ...base, platform: "", attachGameId: null }, SWITCH_ENTRY), null);
  assert.equal(
    buildConsoleImportDecision({ ...base, platform: CONSOLE_IMPORT_PLATFORM_OTHER, attachGameId: null }, SWITCH_ENTRY),
    null
  );
});

test("consoleImportPlatformGroups: primero lo sugerido, sin repetir en el catálogo", () => {
  const groups = consoleImportPlatformGroups([
    { slug: "switch", displayName: "Nintendo Switch", source: "Nintendo Switch" },
    { slug: CONSOLE_IMPORT_PLATFORM_OTHER, displayName: "Otra", source: "Otra" }
  ]);

  assert.deepEqual(groups.suggested, [{ value: "switch", label: "Nintendo Switch" }]);
  assert.equal(groups.catalog.some((option) => option.value === "switch"), false);
  assert.equal(groups.catalog.some((option) => option.value === CONSOLE_IMPORT_PLATFORM_OTHER), false);
  assert.equal(groups.catalog[0]?.value, "ps5");
  assert.equal(groups.catalog[0]?.label, "PlayStation 5");
});

test("consoleImportPlatformGroups: una sugerencia de tienda no se ofrece como opción", () => {
  const groups = consoleImportPlatformGroups([
    { slug: "steam", displayName: "Steam", source: "Steam" },
    { slug: "switch", displayName: "Nintendo Switch", source: "Nintendo Switch" }
  ]);

  assert.deepEqual(groups.suggested, [{ value: "switch", label: "Nintendo Switch" }]);
  assert.equal(groups.catalog.some((option) => option.value === "steam"), false);
});

// --- normalizeConsoleImportCommit ------------------------------------------------------------------

test("normalizeConsoleImportCommit: parsea un commit aplicado", () => {
  const report = normalizeConsoleImportCommit({
    applied: true,
    created: 1,
    attached: 1,
    alreadyPresent: 1,
    entries: [
      { entryId: "c1", outcome: "created", platform: "switch", gameId: 10, userLibraryId: 100 },
      { entryId: "c2", outcome: "attached", platform: "psp", gameId: 42, userLibraryId: 101 },
      { entryId: "c3", outcome: "already_present", platform: "ds", gameId: 7, userLibraryId: 99 }
    ],
    conflicts: []
  });

  assert.ok(report);
  assert.equal(report.applied, true);
  assert.equal(report.entries.length, 3);
  assert.equal(report.entries[0]?.outcome, "created");
  assert.equal(report.entries[1]?.userLibraryId, 101);
});

test("normalizeConsoleImportCommit: un rechazo trae los conflictos y ningún resultado", () => {
  const report = normalizeConsoleImportCommit({
    applied: false,
    created: 0,
    attached: 0,
    alreadyPresent: 0,
    entries: [],
    conflicts: [{ entryId: "c2", platform: "switch", gameId: 42, ownerGameId: 57 }]
  });

  assert.ok(report);
  assert.equal(report.applied, false);
  assert.deepEqual(report.conflicts, [{ entryId: "c2", platform: "switch", gameId: 42, ownerGameId: 57 }]);
});

test("normalizeConsoleImportCommit: rechaza outcomes desconocidos y campos faltantes", () => {
  const base = { applied: true, created: 0, attached: 0, alreadyPresent: 0, conflicts: [] };

  assert.equal(normalizeConsoleImportCommit({ ...base, entries: [{ entryId: "c1", outcome: "raro", platform: "switch", gameId: 1 }] }), null);
  assert.equal(normalizeConsoleImportCommit({ ...base, entries: [{ entryId: "c1", outcome: "created", platform: "switch", gameId: 0 }] }), null);
  assert.equal(normalizeConsoleImportCommit({ applied: true, created: 0, attached: 0, entries: [], conflicts: [] }), null);
  assert.equal(normalizeConsoleImportCommit(null), null);
});
