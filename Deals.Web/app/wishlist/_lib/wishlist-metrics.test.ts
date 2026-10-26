import test from "node:test";
import assert from "node:assert/strict";
import { DISCOUNT_WEIGHT, SCORE_MAX, dealScore, discountPercent, wishlistWinner } from "./wishlist-metrics.ts";

// Base de 10000 (MX$100) con umbral 50% y techo 90%: cada caso deja el factor de descuento
// predecible para poder aislar el bonus por mínimo histórico.
const base = { basePriceMinor: 10_000, baseCurrency: "MXN", minViableDiscountPercent: 50 };
const noHistory = { historyLowMinor: null, historyLowCurrency: null };

const prices = { bestOfficialMinor: 2000, bestKeyshopMinor: 1000 };
test("winner: lower keyshop retains its own metadata", () => {
  assert.deepEqual(wishlistWinner({ ...prices, bestOfficialSource: "steam", bestKeyshopSource: "ggdeals", bestKeyshopLabel: "GG", bestKeyshopClassification: "keyshop", bestKeyshopPricingType: "regional" }),
    { priceMinor: 1000, source: "ggdeals", label: "GG", classification: "keyshop", pricingType: "regional", observedAt: null, badge: "Keyshop" });
});

// El precio que se pinta en la tarjeta y la fecha del distintivo que lo acompaña salen de la misma banda. Si
// divergen, la fila dice «Mín. keys $9.00» con la fecha del precio oficial: el error que ya pasó dos veces en
// la página del juego al pintar el nombre de una tienda sobre el precio de Steam.
test("winner: la fecha observada viaja con el importe ganador, no con el de la otra banda", () => {
  const observed = {
    bestOfficialObservedAt: "2026-10-10T10:00:00Z",
    bestKeyshopObservedAt: "2026-10-17T10:00:00Z"
  };

  // Gana keyshop (más barato): su fecha.
  const keyshop = wishlistWinner({ ...prices, ...observed });
  assert.equal(keyshop?.priceMinor, 1000);
  assert.equal(keyshop?.observedAt, "2026-10-17T10:00:00Z");

  // Gana oficial por empate: la fecha oficial, aunque la de keyshop sea más reciente.
  const official = wishlistWinner({ ...prices, ...observed, bestKeyshopMinor: 2000 });
  assert.equal(official?.observedAt, "2026-10-10T10:00:00Z");

  // Una banda sin ganador no presta su fecha: gana la oficial (empate) y solo la de keyshop tiene fecha, así
  // que el ganador se queda sin fecha en vez de tomar prestada la de la otra banda.
  const noDate = wishlistWinner({ ...prices, bestKeyshopMinor: 2000, bestKeyshopObservedAt: "2026-10-17T10:00:00Z" });
  assert.equal(noDate?.source, null);
  assert.equal(noDate?.observedAt, null);
});
test("winner: official wins ties; Steam requires explicit source", () => {
  const winner = wishlistWinner({ ...prices, bestOfficialMinor: 1000, bestOfficialSource: "steam", bestOfficialClassification: "official" });
  assert.equal(winner?.badge, "Steam");
  assert.equal(winner?.source, "steam");
  assert.equal(wishlistWinner({ ...prices, bestKeyshopMinor: null })?.badge, "Sin clasificar");
});
test("winner: authorized and official labels follow classification, not band", () => {
  assert.equal(wishlistWinner({ ...prices, bestKeyshopMinor: null, bestOfficialClassification: "authorized" })?.badge, "Tienda autorizada");
  assert.equal(wishlistWinner({ ...prices, bestKeyshopMinor: null, bestOfficialClassification: "official" })?.badge, "Tienda oficial");
});
test("winner: no price has no badge; zero is valid in either band", () => {
  assert.equal(wishlistWinner({ bestOfficialMinor: null, bestKeyshopMinor: null }), null);
  assert.equal(wishlistWinner({ ...prices, bestOfficialMinor: 0 })?.priceMinor, 0);
  assert.equal(wishlistWinner({ bestOfficialMinor: null, bestKeyshopMinor: 0 })?.priceMinor, 0);
});
test("winner: unknown old metadata stays unknown; FX metadata survives", () => {
  const winner = wishlistWinner({ ...prices, bestKeyshopPricingType: "fx_estimate" });
  assert.equal(winner?.badge, "Sin clasificar");
  assert.equal(winner?.source, null);
  assert.equal(winner?.pricingType, "fx_estimate");
  assert.equal(discountPercent(10000, "MXN", winner!.priceMinor), 90);
  assert.equal(dealScore({ ...base, ...noHistory, bestMinor: winner!.priceMinor }), 7);
});

test("discountPercent: null cuando el precio base no es MXN", () => {
  assert.equal(discountPercent(10_000, "USD", 5_000), null);
  assert.equal(discountPercent(10_000, null, 5_000), null);
});

test("discountPercent: null cuando falta el mejor precio o el base no es positivo", () => {
  assert.equal(discountPercent(10_000, "MXN", null), null);
  assert.equal(discountPercent(0, "MXN", 0), null);
  assert.equal(discountPercent(-100, "MXN", 0), null);
});

test("discountPercent: redondeo a un decimal y 0 es un precio válido (gratis)", () => {
  assert.equal(discountPercent(10_000, "MXN", 2_500), 75);
  assert.equal(discountPercent(9_000, "MXN", 1_000), 88.9);
  assert.equal(discountPercent(10_000, "MXN", 0), 100);
});

test("dealScore: null cuando no hay moneda MXN o no hay mejor precio", () => {
  assert.equal(dealScore({ ...base, ...noHistory, baseCurrency: "USD", bestMinor: 5_000 }), null);
  assert.equal(dealScore({ ...base, ...noHistory, bestMinor: null }), null);
});

test("dealScore: por debajo del umbral sin mínimo histórico solo suma 0", () => {
  // 40% de descuento queda por debajo del umbral de 50%.
  assert.equal(dealScore({ ...base, ...noHistory, bestMinor: 6_000 }), 0);
});

test("dealScore: en el umbral exacto el descuento aporta 0", () => {
  assert.equal(dealScore({ ...base, ...noHistory, bestMinor: 5_000 }), 0);
});

test("dealScore: un descuento de 90% o más completa la escala de 7 puntos", () => {
  const score = dealScore({ ...base, ...noHistory, bestMinor: 1_000 });
  assert.equal(score, DISCOUNT_WEIGHT);
  assert.equal(score, 7);
});

test("dealScore: estar en el mínimo histórico añade 3 puntos", () => {
  const score = dealScore({ ...base, bestMinor: 1_000, historyLowMinor: 1_000, historyLowCurrency: "MXN" });
  assert.equal(score, SCORE_MAX);
});

test("dealScore: un juego gratis (mejor precio 0) llega a 10", () => {
  const score = dealScore({ ...base, bestMinor: 0, historyLowMinor: 1_000, historyLowCurrency: "MXN" });
  assert.equal(score, SCORE_MAX);
});

test("dealScore: el bonus por mínimo histórico decae al superarlo", () => {
  const onLow = dealScore({ ...base, bestMinor: 1_000, historyLowMinor: 1_000, historyLowCurrency: "MXN" });
  const halfwayLow = dealScore({ ...base, bestMinor: 1_000, historyLowMinor: 500, historyLowCurrency: "MXN" });
  const noLow = dealScore({ ...base, ...noHistory, bestMinor: 1_000 });

  assert.equal(onLow, 10);
  assert.equal(halfwayLow, 8.5);
  assert.equal(noLow, 7);
  assert.ok(onLow !== null && halfwayLow !== null && onLow > halfwayLow && halfwayLow > noLow);
});

test("dealScore: por debajo del umbral, la cercanía al mínimo histórico sigue sumando", () => {
  // 40% de descuento (por debajo del umbral) pero el mejor precio ya es el mínimo histórico.
  assert.equal(dealScore({ ...base, bestMinor: 6_000, historyLowMinor: 6_000, historyLowCurrency: "MXN" }), 3);
});

test("dealScore: un mínimo histórico en otra moneda no aporta bonus", () => {
  assert.equal(dealScore({ ...base, bestMinor: 1_000, historyLowMinor: 1_000, historyLowCurrency: "USD" }), 7);
});

test("dealScore: umbral por encima del techo exige el descuento completo", () => {
  assert.equal(dealScore({ ...base, ...noHistory, bestMinor: 1_500, minViableDiscountPercent: 95 }), 0);
  assert.equal(dealScore({ ...base, ...noHistory, bestMinor: 400, minViableDiscountPercent: 95 }), DISCOUNT_WEIGHT);
});
