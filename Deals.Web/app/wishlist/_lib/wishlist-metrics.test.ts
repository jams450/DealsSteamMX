import test from "node:test";
import assert from "node:assert/strict";
import { DISCOUNT_WEIGHT, SCORE_MAX, dealScore, discountPercent } from "./wishlist-metrics.ts";

// Base de 10000 (MX$100) con umbral 50% y techo 90%: cada caso deja el factor de descuento
// predecible para poder aislar el bonus por mínimo histórico.
const base = { basePriceMinor: 10_000, baseCurrency: "MXN", minViableDiscountPercent: 50 };
const noHistory = { historyLowMinor: null, historyLowCurrency: null };

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
