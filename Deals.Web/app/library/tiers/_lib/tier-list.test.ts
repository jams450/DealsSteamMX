import test from "node:test";
import assert from "node:assert/strict";
import {
  applyTierPlacement,
  createVersionGate,
  moveTierEntry,
  nextPosition,
  sortTierEntries,
  UNRANKED_POSITION
} from "./tier-list.ts";
import type { GameTierEntry } from "../../../../lib/contracts/game-tiers.ts";

// Una sola partida reseñada, rankeable de forma independiente (S5).
const card = (reviewId: number, overrides: Partial<GameTierEntry> = {}): GameTierEntry => ({
  reviewId,
  gameId: 7,
  title: "Portal",
  imageUrl: null,
  platform: "steam",
  isGoty: false,
  tier: "Unranked",
  position: 2147483647,
  ...overrides
});

test("cards of one game stay independent and ordered by position then reviewId", () => {
  const entries = [card(13, { tier: "S", position: 0 }), card(11, { tier: "S", position: 0 }), card(12, { tier: "S", position: 0 })];
  // Misma posición (el caso de la réplica): el reviewId rompe el empate sin mirar el título, así que el
  // orden es estable entre recargas.
  assert.deepEqual(sortTierEntries(entries).map((entry) => entry.reviewId), [11, 12, 13]);
  assert.deepEqual(sortTierEntries([card(13, { position: 2 }), card(11, { position: 9 })]).map((entry) => entry.reviewId), [13, 11]);
  assert.deepEqual(sortTierEntries([]), []);
});
test("UNRANKED_POSITION matches the server int.MaxValue for unranked cards", () => {
  assert.equal(UNRANKED_POSITION, 2147483647);
});

test("next position counts only the target tier", () => {
  const entries = [card(11, { tier: "A", position: 0 }), card(12, { tier: "A", position: 4 }), card(13, { tier: "B", position: 9 })];
  assert.equal(nextPosition(entries, "A"), 5);
  assert.equal(nextPosition(entries, "S"), 0);
  assert.equal(nextPosition([], "F"), 0);
});
test("moving a card writes the new tier and appends it, leaving the others untouched", () => {
  const entries = [card(11, { tier: "A", position: 0 }), card(12, { tier: "A", position: 1 })];
  const moved = moveTierEntry(entries, 12, "S");
  assert.deepEqual(moved, {
    entries: [card(11, { tier: "A", position: 0 }), card(12, { tier: "S", position: 0 })],
    position: 0
  });
  // Mover dos veces ya no toca la tarjeta que quedó en A.
  assert.deepEqual(nextPosition(moved!.entries, "A"), 1);
  // Soltar en el mismo rango no reordena ni guarda nada.
  assert.equal(moveTierEntry(entries, 11, "A"), null);
  // Un id que no está en la lista no puede inventar una tarjeta.
  assert.equal(moveTierEntry(entries, 99, "S"), null);
});
test("a saved placement applies to exactly one review id", () => {
  const entries = [card(11), card(12)];
  assert.deepEqual(applyTierPlacement(entries, 12, "B", 3), [card(11), card(12, { tier: "B", position: 3 })]);
  assert.deepEqual(applyTierPlacement(entries, 99, "B", 3), entries);
});
test("version gate keeps only the newest request or save", () => {
  const gate = createVersionGate();
  const first = gate.begin();
  assert.equal(gate.isCurrent(first), true);
  const second = gate.begin();
  // Una respuesta del año anterior que llega tarde no puede pisar la del año actual.
  assert.equal(gate.isCurrent(first), false);
  assert.equal(gate.isCurrent(second), true);
});
