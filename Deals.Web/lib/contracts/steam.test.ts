import test from "node:test";
import assert from "node:assert/strict";
import { normalizeSteamGame, type SteamGame } from "./steam.ts";

// Respuesta mínima válida: sin ella `normalizeSteamGame` no llega a `ownership`.
const baseGame = {
  appId: 1091500,
  name: "Cyberpunk 2077",
  isFree: false,
  currency: "MXN",
  initialPriceMinor: 59900,
  currentPriceMinor: 59900,
  discountPercent: null,
  lowestPriceMinor: null,
  lowestPriceAt: null,
  region: "mx",
  observedAt: "2026-09-18T10:00:00Z",
  offers: [],
  offersRefreshedAt: null,
  offersStale: false,
  ggDealsRefreshedAt: null,
  ggDealsStale: false,
  bundles: [],
  bundlesRefreshedAt: null,
  bundlesStale: false
};

function ownership(input: unknown) {
  const game = normalizeSteamGame({ ...baseGame, ownership: input }) as SteamGame;
  return game?.ownership;
}

test("ownership: una respuesta vieja (sin la propiedad) es nula y no bloquea la página", () => {
  const game = normalizeSteamGame(baseGame) as SteamGame;
  assert.deepEqual(game?.ownership, { ownedStores: [], hasGamePass: false, possibleMatchStores: [] });
});

test("ownership: una forma inválida degrada a vacío, nunca inventa coincidencias", () => {
  assert.deepEqual(ownership(null), { ownedStores: [], hasGamePass: false, possibleMatchStores: [] });
  assert.deepEqual(ownership("owned"), { ownedStores: [], hasGamePass: false, possibleMatchStores: [] });
  assert.deepEqual(ownership(42), { ownedStores: [], hasGamePass: false, possibleMatchStores: [] });
});

test("ownership: alias de tienda mapeados, slugs de plataforma aceptados, basura descartada", () => {
  assert.deepEqual(ownership({
    ownedStores: ["gog", "GOG", "gog", "epic games", "ubisoft connect", "battle.net", "switch", "", 7, null]
  }), {
    ownedStores: ["gog", "epic", "ubisoft", "battlenet", "switch"],
    hasGamePass: false,
    possibleMatchStores: []
  });
});

test("ownership: `steam` nunca entra, porque la página ya es Steam", () => {
  assert.deepEqual(ownership({ ownedStores: ["steam", "gog"] })?.ownedStores, ["gog"]);
});

test("ownership: `hasGamePass` solo con el literal `true`", () => {
  assert.equal(ownership({ hasGamePass: true })?.hasGamePass, true);
  assert.equal(ownership({ hasGamePass: "true" })?.hasGamePass, false);
  assert.equal(ownership({ hasGamePass: 1 })?.hasGamePass, false);
});

test("ownership: las candidatas por título se validan igual que las confirmadas", () => {
  assert.deepEqual(ownership({ possibleMatchStores: ["humble", "amazon", "Humble", 7] }), {
    ownedStores: [],
    hasGamePass: false,
    possibleMatchStores: ["humble", "amazon"]
  });
});

test("ownership: keep existing fields byte-identical", () => {
  const game = normalizeSteamGame({ ...baseGame, ownership: { ownedStores: ["gog"] } }) as SteamGame;
  assert.equal(game?.appId, 1091500);
  assert.equal(game?.name, "Cyberpunk 2077");
  assert.equal(game?.currentPriceMinor, 59900);
  assert.equal(game?.currency, "MXN");
  assert.deepEqual(game?.offers, []);
  assert.deepEqual(game?.bundles, []);
});

test("reviews: un payload viejo sin reseñas da arreglo vacío, nunca undefined", () => {
  const game = normalizeSteamGame(baseGame) as SteamGame;
  assert.deepEqual(game?.reviews, []);
});

test("reviews: normaliza las reseñas del juego y descarta las inválidas", () => {
  const game = normalizeSteamGame({
    ...baseGame,
    reviews: [
      { reviewId: 1, gameId: 100, platform: "gog", score: 90, scoreLabel: "obra maestra", isGoty: true },
      { reviewId: 2, gameId: 100, platform: "origin store" } // espacio → no es slug, se descarta
    ]
  }) as SteamGame;
  assert.equal(game?.reviews.length, 1);
  assert.equal(game?.reviews[0]?.score, 90);
  assert.equal(game?.reviews[0]?.isGoty, true);
});
