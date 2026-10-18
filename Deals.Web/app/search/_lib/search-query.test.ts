import test from "node:test";
import assert from "node:assert/strict";
import {
  MIN_QUERY_LENGTH,
  SEARCH_MIN_LENGTH_MESSAGE,
  resolveSearchRequest,
  shouldSuggest
} from "./search-query.ts";

// Regresión de propagación: lo que el front manda al backend es la consulta recortada, y una consulta
// demasiado corta no viaja. El backend ya vuelve a recortar, pero el contrato del cliente empieza aquí.
test("la consulta se recorta antes de viajar al backend", () => {
  assert.deepEqual(resolveSearchRequest("  Hades  "), { query: "Hades", error: null });
});

test("una consulta válida de exactamente el mínimo pasa", () => {
  assert.equal(MIN_QUERY_LENGTH, 2);
  assert.deepEqual(resolveSearchRequest("ab"), { query: "ab", error: null });
});

test("menos del mínimo no consulta y conserva el mensaje en español", () => {
  assert.deepEqual(resolveSearchRequest(" h "), { query: "h", error: SEARCH_MIN_LENGTH_MESSAGE });
  assert.deepEqual(resolveSearchRequest(""), { query: "", error: SEARCH_MIN_LENGTH_MESSAGE });
});

test("las sugerencias piden solo con consulta suficiente y distinta a la ya buscada", () => {
  assert.equal(shouldSuggest("had", null), true);
  assert.equal(shouldSuggest("h", null), false);
  assert.equal(shouldSuggest("hades", "hades"), false);
  assert.equal(shouldSuggest("hades", "had"), true);
});
