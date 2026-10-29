import test from "node:test";
import assert from "node:assert/strict";
import { GAME_TIERS, normalizeGameTierList, normalizeGameTierYears, parseGameTierUpdate } from "./game-tiers.ts";

test("annual tiers use exactly S,A,B,C,D,F and reject E", () => {
  assert.deepEqual(GAME_TIERS, ["S", "A", "B", "C", "D", "F"]);
  for (const tier of GAME_TIERS) assert.deepEqual(parseGameTierUpdate({ reviewId: 1, tier, position: 0 }), { reviewId: 1, tier, position: 0 });
  assert.equal(parseGameTierUpdate({ reviewId: 1, tier: "E", position: 0 }), null);
});
test("placement validation rejects invalid review identity and position boundaries", () => {
  // La partida reseñada es la identidad: un payload con gameId no dice cuál de dos reseñas del mismo
  // juego se movió, así que solo un reviewId positivo identifica el puesto.
  for (const reviewId of [0, -1, 1.5, "1", null, undefined]) assert.equal(parseGameTierUpdate({ reviewId, tier: "A", position: 0 }), null);
  for (const position of [-1, 10001, 0.5, "0", null]) assert.equal(parseGameTierUpdate({ reviewId: 1, tier: "A", position }), null);
  assert.equal(parseGameTierUpdate({ reviewId: 1, tier: "F", position: 10000 })?.position, 10000);
  assert.equal(parseGameTierUpdate({ gameId: 7, tier: "A", position: 0 }), null);
});
test("annual payload keys cards by review so one game can appear twice", () => {
  const first = { reviewId: 11, gameId: 7, title: "Portal", imageUrl: null, platform: "steam", isGoty: false, tier: "Unranked", position: 2147483647 };
  const replay = { reviewId: 12, gameId: 7, title: "Portal", imageUrl: null, platform: "ps5", isGoty: true, tier: "S", position: 0 };
  // Dos partidas del mismo juego en el mismo año son dos tarjetas independientes (S5).
  assert.deepEqual(normalizeGameTierList([first, replay]), [first, replay]);
  assert.deepEqual(normalizeGameTierList([]), []);
  // Duplicar la identidad de la tarjeta sí es un payload inválido: el reviewId es la llave.
  assert.equal(normalizeGameTierList([first, { ...replay, reviewId: first.reviewId }]), null);
  assert.equal(normalizeGameTierList([{ ...first, tier: "E" }]), null);
  assert.equal(normalizeGameTierList({ items: [] }), null);
});
test("card payload normalizes platform, goty and optional cover", () => {
  const base = { reviewId: 11, gameId: 7, title: "Portal", imageUrl: null, platform: "steam", isGoty: false, tier: "S", position: 0 };
  const [card] = normalizeGameTierList([{ ...base, platform: " Steam ", imageUrl: "https://cdn.example/portal.jpg" }]) ?? [];
  assert.equal(card?.platform, "steam");
  assert.equal(card?.imageUrl, "https://cdn.example/portal.jpg");
  // Solo el literal `true` marca GOTY, igual que en el contrato de reseñas.
  assert.deepEqual(normalizeGameTierList([{ ...base, platform: "ps5", isGoty: "true" }])?.[0]?.isGoty, false);
  assert.deepEqual(normalizeGameTierList([{ ...base, platform: "ps5", isGoty: true }])?.[0]?.isGoty, true);
  assert.equal(normalizeGameTierList([{ ...base, platform: "not a platform!" }]), null);
  assert.equal(normalizeGameTierList([{ ...base, imageUrl: "http://insecure.example/x.jpg" }])?.[0]?.imageUrl, null);
});
test("review years are distinct, newest first and reject malformed lists", () => {
  assert.deepEqual(normalizeGameTierYears([2024, 2026, 2025, 2026]), [2026, 2025, 2024]);
  assert.deepEqual(normalizeGameTierYears([]), []);
  for (const invalid of [null, undefined, "2026", { years: [] }, [2026, "2025"], [2026, 999], [2026, 10000], [2026, 2025.5]]) {
    assert.equal(normalizeGameTierYears(invalid), null);
  }
});
