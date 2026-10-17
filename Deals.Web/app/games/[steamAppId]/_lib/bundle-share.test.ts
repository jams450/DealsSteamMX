import assert from "node:assert/strict";
import test from "node:test";
import { compareShareToStandalone, gameTierComparison, tierMembership, tierShare } from "./bundle-share.ts";
import type { SteamBundleTier, SteamBundleTierItem } from "@/lib/contracts/steam";

// Reutiliza la forma completa del contrato para no acoplar la prueba a un objeto parcial: el módulo
// puro solo mira los campos que le importan, y el resto existe para que el tipo sea el real.
function tier(overrides: Partial<SteamBundleTier> = {}): SteamBundleTier {
  return {
    priceMinor: 1000,
    currency: "USD",
    addon: false,
    itemsComplete: true,
    games: [],
    status: null,
    reason: null,
    individualTotalMinor: null,
    bundlePriceMinor: null,
    savingsMinor: null,
    savingsPercent: null,
    fxRate: null,
    fxRateDate: null,
    fxSource: null,
    pricingType: null,
    mxnIndividualTotalMinor: null,
    mxnSavingsMinor: null,
    ...overrides
  };
}

function item(
  title: string,
  type: string | null,
  priceMinor: number | null = 1,
  itadGameId: string | null = null
): SteamBundleTierItem {
  return { title, type, priceMinor, priceCurrency: priceMinor === null ? null : "USD", itadGameId };
}

test("un tier completo con precio reparte el precio entre los juegos", () => {
  const share = tierShare(tier({ games: [item("A", "game"), item("B", "game")] }));
  assert.equal(share.status, "ok");
  assert.equal(share.eligibleGameCount, 2);
  assert.equal(share.itemCount, 2);
  assert.equal(share.allocatedPerGameMinor, 500);
  assert.equal(share.currency, "USD");
  assert.equal(share.requiresWholeTierPurchase, true);
});

test("la division inexacta se redondea a la unidad minima", () => {
  const share = tierShare(tier({ priceMinor: 1001, games: [item("A", "game"), item("B", "game"), item("C", "game")] }));
  assert.equal(share.allocatedPerGameMinor, 334);
});

test("DLC y paquetes no cuentan como juegos elegibles", () => {
  const share = tierShare(tier({ priceMinor: 900, games: [item("A", "game"), item("B", "dlc"), item("C", "package")] }));
  assert.equal(share.eligibleGameCount, 1);
  assert.equal(share.itemCount, 3);
  assert.equal(share.allocatedPerGameMinor, 900);
});

test("un precio 0 es un precio publicado: reparte 0 y no es ausencia", () => {
  const share = tierShare(tier({ priceMinor: 0, games: [item("A", "game")] }));
  assert.equal(share.status, "ok");
  assert.equal(share.allocatedPerGameMinor, 0);
});

test("un tier addon no publica reparto", () => {
  const share = tierShare(tier({ addon: true, games: [item("A", "game")] }));
  assert.equal(share.status, "addon");
  assert.equal(share.allocatedPerGameMinor, null);
  assert.equal(share.requiresWholeTierPurchase, false);
});

test("un tier sin lista completa no publica reparto", () => {
  const share = tierShare(tier({ itemsComplete: false, games: [item("A", "game")] }));
  assert.equal(share.status, "items_incomplete");
  assert.equal(share.allocatedPerGameMinor, null);
});

test("precio desconocido o build your own no publica reparto", () => {
  const share = tierShare(tier({ priceMinor: null, games: [item("A", "game")] }));
  assert.equal(share.status, "no_tier_price");
  assert.equal(share.allocatedPerGameMinor, null);
});

test("precio sin moneda no publica reparto", () => {
  const share = tierShare(tier({ currency: "  ", games: [item("A", "game")] }));
  assert.equal(share.status, "currency_unknown");
  assert.equal(share.allocatedPerGameMinor, null);
});

test("un tier sin juegos elegibles no publica reparto", () => {
  const share = tierShare(tier({ games: [item("A", "dlc")] }));
  assert.equal(share.status, "no_eligible_items");
  assert.equal(share.allocatedPerGameMinor, null);
});

test("sin identidad de item no hay reclamo de mas barato", () => {
  const share = tierShare(tier({ games: [item("A", "game"), item("B", "game")] }));
  assert.equal(compareShareToStandalone(share, 600, "USD", "unknown"), null);
  assert.equal(compareShareToStandalone(share, 600, "USD", false), null);
});

test("con identidad y misma moneda compara el reparto contra el precio suelto", () => {
  const share = tierShare(tier({ games: [item("A", "game"), item("B", "game")] }));
  assert.deepEqual(compareShareToStandalone(share, 600, "USD", true), {
    cheaper: true,
    allocatedPerGameMinor: 500,
    currency: "USD"
  });
  assert.equal(compareShareToStandalone(share, 400, "USD", true)?.cheaper, false);
  assert.equal(compareShareToStandalone(share, 500, "USD", true)?.cheaper, false);
});

test("monedas distintas o precio suelto ausente no se comparan", () => {
  const share = tierShare(tier({ games: [item("A", "game"), item("B", "game")] }));
  assert.equal(compareShareToStandalone(share, 600, "MXN", true), null);
  assert.equal(compareShareToStandalone(share, null, "USD", true), null);
});

test("la comparacion usa la division exacta, no la cifra redondeada", () => {
  // 1001 / 3 = 333.67 y se muestra 334; un precio suelto de 334 sigue siendo mas caro que el reparto real.
  const share = tierShare(tier({ priceMinor: 1001, games: [item("A", "game"), item("B", "game"), item("C", "game")] }));
  assert.equal(share.allocatedPerGameMinor, 334);
  assert.equal(compareShareToStandalone(share, 334, "USD", true)?.cheaper, true);
});

test("un tier no asignable no se compara", () => {
  const share = tierShare(tier({ priceMinor: null, games: [item("A", "game")] }));
  assert.equal(compareShareToStandalone(share, 100, "USD", true), null);
});

// --- Membresía exacta y comparación por juego ---------------------------------------------------

test("tierMembership: gana solo el tier que lista el id exacto del juego", () => {
  const correct = tier({ games: [item("A", "game", 1, "itad-a"), item("B", "game", 1, "itad-b")] });
  const wrong = tier({ games: [item("C", "game", 1, "itad-c")] });
  assert.equal(tierMembership(correct, "itad-a"), true);
  assert.equal(tierMembership(wrong, "itad-a"), false);
});

test("tierMembership: un snapshot viejo sin ids se abstiene, nunca es un falso 'no'", () => {
  const legacy = tier({ games: [item("A", "game", 1, null)] });
  assert.equal(tierMembership(legacy, "itad-a"), "unknown");
  assert.equal(tierMembership(tier({ games: [] }), "itad-a"), "unknown");
  assert.equal(tierMembership(tier({ games: [item("A", "game", 1, "itad-a")] }), null), "unknown");
});

test("tierMembership: la identidad parcial no prueba ausencia", () => {
  const partial = tier({ games: [item("A", "game", 1, "itad-b"), item("B", "game", 1, null)] });
  assert.equal(tierMembership(partial, "itad-a"), "unknown");
});

test("el tier correcto compara el reparto contra el precio suelto", () => {
  const correct = tier({ priceMinor: 1000, games: [item("A", "game", 1, "itad-a"), item("B", "game", 1, "itad-b")] });
  assert.deepEqual(gameTierComparison(correct, 600, "USD", "itad-a"), {
    cheaper: true,
    allocatedPerGameMinor: 500,
    currency: "USD"
  });
  assert.equal(gameTierComparison(correct, 400, "USD", "itad-a")?.cheaper, false);
});

test("el tier equivocado no hace ningún reclamo de precio", () => {
  const wrong = tier({ priceMinor: 1000, games: [item("C", "game", 1, "itad-c")] });
  assert.equal(gameTierComparison(wrong, 600, "USD", "itad-a"), null);
});

test("sin identidad heredada no hay comparación aunque el reparto sea válido", () => {
  const legacy = tier({ priceMinor: 1000, games: [item("A", "game", 1, null), item("B", "game", 1, null)] });
  assert.equal(gameTierComparison(legacy, 600, "USD", "itad-a"), null);
});

test("la comparación por juego usa la división exacta, no la cifra redondeada", () => {
  // 1001 / 3 = 333.67 se muestra 334; un precio suelto de 334 sigue siendo más caro que el reparto real.
  const correct = tier({
    priceMinor: 1001,
    games: [item("A", "game", 1, "itad-a"), item("B", "game", 1, "itad-b"), item("C", "game", 1, "itad-c")]
  });
  assert.equal(gameTierComparison(correct, 334, "USD", "itad-a")?.cheaper, true);
});

test("moneda distinta no se compara aunque el tier sea el correcto", () => {
  const correct = tier({ priceMinor: 1000, currency: "USD", games: [item("A", "game", 1, "itad-a")] });
  assert.equal(gameTierComparison(correct, 600, "MXN", "itad-a"), null);
});
