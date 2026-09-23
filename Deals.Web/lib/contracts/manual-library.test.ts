import assert from "node:assert/strict";
import test from "node:test";
import {
  MANUAL_PLATFORM_OPTIONS,
  normalizeManualAddResponse,
  normalizeManualCandidatesResponse,
  normalizeManualSearchResponse
} from "./manual-library.ts";
import { normalizeStore } from "./stores.ts";

test("parsea los resultados de búsqueda de IGDB con portada, año y plataformas", () => {
  const parsed = normalizeManualSearchResponse({
    source: "igdb",
    hits: [
      {
        igdbId: 967140,
        title: "Metroid Dread",
        releaseYear: 2021,
        imageUrl: "https://images.igdb.com/igdb/image/upload/t_thumb/a.jpg",
        platforms: [
          { id: 130, name: "Nintendo Switch" },
          { id: 20, name: "Nintendo DS" }
        ]
      }
    ]
  });

  assert.ok(parsed);
  assert.equal(parsed.source, "igdb");
  assert.equal(parsed.hits.length, 1);
  assert.equal(parsed.hits[0].igdbId, 967140);
  assert.equal(parsed.hits[0].releaseYear, 2021);
  assert.equal(parsed.hits[0].platforms.length, 2);
});

test("source null (proveedor no disponible) sí parsea: no es lo mismo que respuesta rota", () => {
  assert.deepEqual(normalizeManualSearchResponse({ source: null, hits: [] }), { source: null, hits: [] });
  assert.equal(normalizeManualSearchResponse({ source: "igdb", hits: [{ title: "sin id" }] }), null);
  assert.equal(normalizeManualSearchResponse("garbage"), null);
});

test("parsea el resultado del alta con sus cuatro outcomes", () => {
  for (const outcome of ["created", "attached", "duplicate", "candidates"]) {
    const parsed = normalizeManualAddResponse({
      outcome,
      created: 1,
      attached: 0,
      duplicateRow: 0,
      gameId: 7,
      userLibraryId: 9,
      candidates: []
    });
    assert.ok(parsed, `outcome ${outcome}`);
    assert.equal(parsed.outcome, outcome);
    assert.equal(parsed.gameId, 7);
    assert.equal(parsed.userLibraryId, 9);
  }

  assert.equal(normalizeManualAddResponse({ outcome: "loquesea", created: 0, attached: 0, duplicateRow: 0, gameId: 0, userLibraryId: 0, candidates: [] }), null);
});

test("parsea candidatos y rechaza estructuras rotas", () => {
  const candidates = normalizeManualCandidatesResponse({
    candidates: [{ gameId: 3, title: "Metroid Dread", releaseYear: 2021, imageUrl: null, inLibrary: false }]
  });
  assert.ok(candidates);
  assert.equal(candidates.length, 1);
  assert.equal(candidates[0].inLibrary, false);

  assert.equal(normalizeManualCandidatesResponse({ candidates: "no" }), null);
  assert.equal(normalizeManualCandidatesResponse({ candidates: [{ gameId: -1, title: "x", releaseYear: null, imageUrl: null, inLibrary: false }] }), null);
});

test("toda opción del selector es un store aceptable por el validador único", () => {
  assert.ok(MANUAL_PLATFORM_OPTIONS.length >= 10);
  for (const option of MANUAL_PLATFORM_OPTIONS) {
    assert.equal(normalizeStore(option.value), option.value, option.value);
  }
});
