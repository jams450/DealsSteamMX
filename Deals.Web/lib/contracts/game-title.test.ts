import assert from "node:assert/strict";
import test from "node:test";
import {
  GAME_TITLE_MAX_LENGTH,
  normalizeGameTitleApplied,
  normalizeGameTitleConflict,
  normalizeGameTitleResult,
  parseGameTitleRequest
} from "./game-title.ts";

test("parseGameTitleRequest: modo manual exige un título y prohíbe igdbId", () => {
  assert.deepEqual(parseGameTitleRequest({ mode: "manual", title: "Halo" }), { mode: "manual", title: "Halo" });
  assert.deepEqual(parseGameTitleRequest({ mode: "manual", title: "  Halo  " }), { mode: "manual", title: "Halo" });
  // Un `igdbId` nulo es "no enviado": el backend lo acepta igual en modo manual.
  assert.deepEqual(parseGameTitleRequest({ mode: "manual", title: "Halo", igdbId: null }), { mode: "manual", title: "Halo" });

  assert.equal(parseGameTitleRequest({ mode: "manual", title: "" }), null);
  assert.equal(parseGameTitleRequest({ mode: "manual", title: "   " }), null);
  assert.equal(parseGameTitleRequest({ mode: "manual", title: 12 }), null);
  assert.equal(parseGameTitleRequest({ mode: "manual" }), null);
  // El modo es discriminado: `igdbId` junto a `title` es ambiguo y se rechaza.
  assert.equal(parseGameTitleRequest({ mode: "manual", title: "Halo", igdbId: 5 }), null);
});

test("parseGameTitleRequest: el tope de 512 se rechaza, nunca se recorta", () => {
  const exact = "a".repeat(GAME_TITLE_MAX_LENGTH);
  assert.deepEqual(parseGameTitleRequest({ mode: "manual", title: exact }), { mode: "manual", title: exact });
  assert.equal(parseGameTitleRequest({ mode: "manual", title: `${exact}a` }), null);
  // El recorte ocurre antes del tope (igual que en el backend): 512 letras con aire alrededor son válidas,
  // 513 no lo son por mucho espacio que sobre.
  assert.deepEqual(parseGameTitleRequest({ mode: "manual", title: `  ${exact}  ` }), { mode: "manual", title: exact });
  assert.equal(parseGameTitleRequest({ mode: "manual", title: `  ${exact}a  ` }), null);
});

test("parseGameTitleRequest: modo igdb exige un id entero positivo y prohíbe title", () => {
  assert.deepEqual(parseGameTitleRequest({ mode: "igdb", igdbId: 967140 }), { mode: "igdb", igdbId: 967140 });
  assert.deepEqual(parseGameTitleRequest({ mode: "igdb", igdbId: 967140, title: null }), { mode: "igdb", igdbId: 967140 });

  assert.equal(parseGameTitleRequest({ mode: "igdb" }), null);
  assert.equal(parseGameTitleRequest({ mode: "igdb", igdbId: 0 }), null);
  assert.equal(parseGameTitleRequest({ mode: "igdb", igdbId: -3 }), null);
  assert.equal(parseGameTitleRequest({ mode: "igdb", igdbId: 1.5 }), null);
  assert.equal(parseGameTitleRequest({ mode: "igdb", igdbId: "967140" }), null);
  assert.equal(parseGameTitleRequest({ mode: "igdb", igdbId: 967140, title: "Metroid Dread" }), null);
});

test("parseGameTitleRequest: modo desconocido o cuerpo que no es objeto", () => {
  assert.equal(parseGameTitleRequest({ mode: "auto", title: "Halo" }), null);
  assert.equal(parseGameTitleRequest({ title: "Halo" }), null);
  assert.equal(parseGameTitleRequest([]), null);
  assert.equal(parseGameTitleRequest(null), null);
  assert.equal(parseGameTitleRequest(42), null);
});

test("normalizeGameTitleApplied: 200 manual y 200 igdb", () => {
  const manual = normalizeGameTitleApplied({
    gameId: 7,
    title: "Metroid Dread",
    normalizedTitle: "metroid dread",
    source: "manual"
  });
  assert.deepEqual(manual, {
    kind: "applied",
    gameId: 7,
    title: "Metroid Dread",
    normalizedTitle: "metroid dread",
    source: "manual",
    igdbId: null
  });

  // `igdbId: null` explícito es válido en modo manual; PascalCase también.
  assert.equal(normalizeGameTitleApplied({
    GameId: 7,
    Title: "Metroid Dread",
    NormalizedTitle: "metroid dread",
    Source: "Manual",
    IgdbId: null
  })?.source, "manual");

  assert.deepEqual(normalizeGameTitleApplied({
    gameId: 7,
    title: "Metroid Dread",
    normalizedTitle: "metroid dread",
    source: "igdb",
    igdbId: 967140
  }), {
    kind: "applied",
    gameId: 7,
    title: "Metroid Dread",
    normalizedTitle: "metroid dread",
    source: "igdb",
    igdbId: 967140
  });
});

test("normalizeGameTitleApplied: rechaza formas rotas y el cuerpo de un 409", () => {
  const valid = { gameId: 7, title: "Metroid Dread", normalizedTitle: "metroid dread", source: "manual" };

  assert.equal(normalizeGameTitleApplied({ ...valid, gameId: 0 }), null);
  assert.equal(normalizeGameTitleApplied({ ...valid, title: "   " }), null);
  assert.equal(normalizeGameTitleApplied({ ...valid, normalizedTitle: undefined }), null);
  assert.equal(normalizeGameTitleApplied({ ...valid, source: "steam" }), null);
  // `igdb` sin id no prueba la identidad reclamada; `manual` con id contradice su fuente.
  assert.equal(normalizeGameTitleApplied({ ...valid, source: "igdb" }), null);
  assert.equal(normalizeGameTitleApplied({ ...valid, igdbId: 967140 }), null);
  // El 409 nunca puede leerse como acierto.
  assert.equal(normalizeGameTitleApplied({ applied: false, gameId: 7, reason: "no" }), null);
  assert.equal(normalizeGameTitleApplied(null), null);
});

test("normalizeGameTitleConflict: exige applied false y un motivo mostrable", () => {
  assert.deepEqual(normalizeGameTitleConflict({ applied: false, gameId: 7, reason: "Ese juego ya está vinculado." }), {
    kind: "conflict",
    gameId: 7,
    reason: "Ese juego ya está vinculado."
  });

  // El motivo es lo único que la UI puede mostrar: sin él no hay conflicto interpretable.
  assert.equal(normalizeGameTitleConflict({ applied: false, gameId: 7 }), null);
  assert.equal(normalizeGameTitleConflict({ applied: false, gameId: 7, reason: "  " }), null);
  assert.equal(normalizeGameTitleConflict({ applied: true, gameId: 7, reason: "no" }), null);
  assert.equal(normalizeGameTitleConflict({ gameId: 7, reason: "no" }), null);
  assert.equal(normalizeGameTitleConflict(null), null);

  // Un `gameId` ausente no es motivo para perder el motivo: queda en null.
  const orphan = normalizeGameTitleConflict({ applied: false, reason: "Rechazado." });
  assert.deepEqual(orphan, { kind: "conflict", gameId: null, reason: "Rechazado." });
});

test("normalizeGameTitleResult: `applied` decide la rama", () => {
  const applied = normalizeGameTitleResult({
    gameId: 7,
    title: "Halo",
    normalizedTitle: "halo",
    source: "manual",
    igdbId: null
  });
  assert.equal(applied?.kind, "applied");

  const conflict = normalizeGameTitleResult({ applied: false, gameId: 7, reason: "Ocupado." });
  assert.deepEqual(conflict, { kind: "conflict", gameId: 7, reason: "Ocupado." });

  assert.equal(normalizeGameTitleResult({ applied: false, gameId: 7 }), null);
  assert.equal(normalizeGameTitleResult({ gameId: 7, title: "Halo" }), null);
  assert.equal(normalizeGameTitleResult("garbage"), null);
});
