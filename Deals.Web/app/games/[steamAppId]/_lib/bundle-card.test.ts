import assert from "node:assert/strict";
import test from "node:test";
import { pickPricedBundleTier } from "./bundle-card.ts";
import type { SteamBundleTier, SteamGameBundle } from "@/lib/contracts/steam";

function tier(overrides: Partial<SteamBundleTier> = {}): SteamBundleTier {
  return {
    priceMinor: 19900,
    currency: "MXN",
    addon: false,
    itemsComplete: true,
    games: [],
    status: "ok",
    reason: null,
    individualTotalMinor: null,
    bundlePriceMinor: null,
    savingsMinor: null,
    savingsPercent: null,
    fxRate: null,
    fxRateDate: null,
    fxSource: null,
    pricingType: "regional",
    mxnIndividualTotalMinor: null,
    mxnSavingsMinor: null,
    ...overrides
  };
}

function bundle(title: string, tiers: readonly SteamBundleTier[]): SteamGameBundle {
  return {
    bundleKey: title,
    title,
    shopName: "Humble Store",
    pageUrl: "https://isthereanydeal.com/bundle/x/",
    dealUrl: null,
    details: null,
    publishedAt: null,
    expiresAt: null,
    observedAt: null,
    tiers
  };
}

test("sin bundles no hay tarjeta", () => {
  assert.equal(pickPricedBundleTier([], "MXN"), null);
});

test("un bundle sin precio en ninguno de sus tiers no produce cifra", () => {
  const result = pickPricedBundleTier(
    [bundle("Sin precio", [tier({ priceMinor: null, currency: null })])],
    "MXN"
  );
  assert.equal(result, null);
});

test("elige el tier más barato entre todos los bundles y tiers", () => {
  const result = pickPricedBundleTier(
    [
      bundle("Caro", [tier({ priceMinor: 50000 })]),
      bundle("Barato", [tier({ priceMinor: 9900 }), tier({ priceMinor: 12000 })])
    ],
    "MXN"
  );
  assert.equal(result?.bundle.title, "Barato");
  assert.equal(result?.priceMinor, 9900);
  assert.equal(result?.currency, "MXN");
});

test("prefiere MXN y no compara cifras de monedas distintas", () => {
  // 1999 USD es mucho más caro que 39900 MXN en la realidad, y aun así 1999 < 39900 como números: sin el
  // filtro por moneda, el ganador sería el tier en USD y la tarjeta mostraría el precio equivocado.
  const result = pickPricedBundleTier(
    [bundle("Mixto", [tier({ priceMinor: 1999, currency: "USD" }), tier({ priceMinor: 39900 })])],
    "MXN"
  );
  assert.equal(result?.currency, "MXN");
  assert.equal(result?.priceMinor, 39900);
});

test("sin tiers en MXN devuelve el de otra moneda, sin convertir", () => {
  const result = pickPricedBundleTier(
    [bundle("Solo USD", [tier({ priceMinor: 1999, currency: "USD" })])],
    "MXN"
  );
  assert.equal(result?.currency, "USD");
  assert.equal(result?.priceMinor, 1999);
});

test("un tier sin precio se ignora y el gratis es el más barato", () => {
  const result = pickPricedBundleTier(
    [
      bundle("Parcial", [
        tier({ priceMinor: null, currency: null }),
        tier({ priceMinor: 0 }),
        tier({ priceMinor: 25000 })
      ])
    ],
    "MXN"
  );
  // 0 es un precio publicado (el componente lo pinta «Gratis»), no la ausencia de precio.
  assert.equal(result?.priceMinor, 0);
});
