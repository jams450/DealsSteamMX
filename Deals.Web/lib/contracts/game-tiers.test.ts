import test from "node:test";
import assert from "node:assert/strict";
import { GAME_TIERS, normalizeGameTierList, parseGameTierUpdate } from "./game-tiers.ts";

test("annual tiers use exactly S,A,B,C,D,F and reject E", () => {
  assert.deepEqual(GAME_TIERS, ["S", "A", "B", "C", "D", "F"]);
  for (const tier of GAME_TIERS) assert.deepEqual(parseGameTierUpdate({ gameId: 1, tier, position: 0 }), { gameId: 1, tier, position: 0 });
  assert.equal(parseGameTierUpdate({ gameId: 1, tier: "E", position: 0 }), null);
});
test("placement validation rejects invalid identity and position boundaries", () => {
  for (const gameId of [0, -1, 1.5, "1", null]) assert.equal(parseGameTierUpdate({ gameId, tier: "A", position: 0 }), null);
  for (const position of [-1, 10001, 0.5, "0", null]) assert.equal(parseGameTierUpdate({ gameId: 1, tier: "A", position }), null);
  assert.equal(parseGameTierUpdate({ gameId: 1, tier: "F", position: 10000 })?.position, 10000);
});
test("annual payload keeps unranked games, rejects duplicates and malformed responses", () => {
  const entry = { gameId: 1, title: "Portal", imageUrl: null, tier: "Unranked", position: 2147483647 };
  assert.deepEqual(normalizeGameTierList([entry]), [entry]);
  assert.deepEqual(normalizeGameTierList([]), []);
  assert.equal(normalizeGameTierList([entry, entry]), null);
  assert.equal(normalizeGameTierList([ { ...entry, tier: "E" } ]), null);
  assert.equal(normalizeGameTierList({ items: [] }), null);
});
