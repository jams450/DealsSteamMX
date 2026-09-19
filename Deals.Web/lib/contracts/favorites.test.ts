import test from "node:test";
import assert from "node:assert/strict";
import { parseFavoriteTarget } from "./favorites.ts";

test("parseFavoriteTarget: acepta una sola identidad válida", () => {
  assert.deepEqual(parseFavoriteTarget({ gameId: 42 }), { gameId: 42 });
  assert.deepEqual(parseFavoriteTarget({ steamAppId: 1091500 }), { steamAppId: 1091500 });
});

test("parseFavoriteTarget: rechaza ninguna, las dos o valores inválidos", () => {
  assert.equal(parseFavoriteTarget(null), null);
  assert.equal(parseFavoriteTarget({}), null);
  // Dos identidades se rechazan aunque una sea basura: nunca se elige una por el cliente.
  assert.equal(parseFavoriteTarget({ gameId: 42, steamAppId: 1091500 }), null);
  assert.equal(parseFavoriteTarget({ gameId: 42, steamAppId: "x" }), null);
  assert.equal(parseFavoriteTarget({ gameId: 0 }), null);
  assert.equal(parseFavoriteTarget({ gameId: -1 }), null);
  assert.equal(parseFavoriteTarget({ gameId: 4.5 }), null);
  assert.equal(parseFavoriteTarget({ gameId: "42" }), null);
  assert.equal(parseFavoriteTarget({ steamAppId: Number.MAX_SAFE_INTEGER + 2 }), null);
});
