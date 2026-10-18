import test from "node:test";
import assert from "node:assert/strict";
import {
  normalizeGlobalDealCatalog,
  parseGlobalDealPageParam,
  MAX_GLOBAL_DEALS,
  GLOBAL_DEALS_PAGE_SIZE_DEFAULT,
  GLOBAL_DEALS_PAGE_SIZE_MAX
} from "./global-deals.ts";

function deal(overrides: Record<string, unknown> = {}) {
  return {
    dealKey: "deal-1",
    itadGameId: "11111111-1111-1111-1111-111111111111",
    gameTitle: "Game A",
    gameSlug: "game-a",
    gameType: "game",
    gameMature: false,
    shopId: "61",
    shopName: "Steam",
    currency: "USD",
    currentPriceMinor: 1999,
    regularPriceMinor: 5999,
    discountPercent: 67,
    mxnCurrentPriceMinor: 36982,
    pricingType: "fx_estimate",
    fxRate: 18.5,
    fxRateDate: "2026-10-17",
    fxSource: "banxico",
    historyLowMinor: null,
    historyLowCurrency: null,
    isAtHistoryLow: false,
    goodDealReason: "discount",
    dealUrl: "https://isthereanydeal.com/deal/deal-1?ref=aff",
    expiresAt: "2999-11-01T00:00:00Z",
    observedAt: "2026-10-17T00:00:00Z",
    ...overrides
  };
}

test("una carga vacia o invalida es un feed vacio, nunca un error de render", () => {
  for (const input of [null, undefined, 7, "x", {}, { items: "no" }]) {
    const catalog = normalizeGlobalDealCatalog(input);
    assert.deepEqual(catalog.items, []);
    assert.equal(catalog.count, 0);
    assert.equal(catalog.total, 0);
    assert.equal(catalog.page, 1);
    assert.equal(catalog.pageSize, GLOBAL_DEALS_PAGE_SIZE_DEFAULT);
    assert.equal(catalog.hasMore, false);
  }
});

test("una oferta valida conserva la moneda original y la clasificacion", () => {
  const catalog = normalizeGlobalDealCatalog({ items: [deal()] });
  const item = catalog.items[0];
  assert.equal(item.currency, "USD");
  assert.equal(item.currentPriceMinor, 1999);
  assert.equal(item.pricingType, "fx_estimate");
  assert.equal(item.mxnCurrentPriceMinor, 36982);
  assert.equal(item.dealUrl, "https://isthereanydeal.com/deal/deal-1?ref=aff");
});

test("un feed no inventa nota ni rating: solo expone el codigo de motivo real", () => {
  const catalog = normalizeGlobalDealCatalog({ items: [deal()] });
  const item = catalog.items[0] as unknown as Record<string, unknown>;
  assert.equal("score" in item, false);
  assert.equal("rating" in item, false);
  assert.equal(item.goodDealReason, "discount");

  const unknown = normalizeGlobalDealCatalog({ items: [deal({ goodDealReason: "top_rated" })] });
  assert.equal(unknown.items[0].goodDealReason, null);
  const absent = normalizeGlobalDealCatalog({ items: [deal({ goodDealReason: null })] });
  assert.equal(absent.items[0].goodDealReason, null);
});

test("una oferta sin MXN no recibe una cifra cuando no hay conversion", () => {
  const catalog = normalizeGlobalDealCatalog({
    items: [deal({ currency: "EUR", pricingType: "unconverted", mxnCurrentPriceMinor: 12345 })]
  });
  assert.equal(catalog.items[0].currency, "EUR");
  assert.equal(catalog.items[0].mxnCurrentPriceMinor, null);
});

test("una clasificacion o una oferta incompleta se descartan", () => {
  const catalog = normalizeGlobalDealCatalog({
    items: [
      deal({ pricingType: "guess" }),
      deal({ dealKey: "" }),
      deal({ currency: "" }),
      deal({ currentPriceMinor: -1 }),
      deal()
    ]
  });
  assert.equal(catalog.items.length, 1);
  assert.equal(catalog.items[0].dealKey, "deal-1");
});

test("un descuento fuera de rango no se muestra como dato", () => {
  const catalog = normalizeGlobalDealCatalog({ items: [deal({ discountPercent: 150 })] });
  assert.equal(catalog.items[0].discountPercent, null);
});

test("el tope de ofertas se respeta y la paginacion del servidor se conserva", () => {
  const many = Array.from({ length: MAX_GLOBAL_DEALS + 5 }, (_, index) => deal({ dealKey: `deal-${index}` }));
  const clamped = normalizeGlobalDealCatalog({ items: many });
  assert.equal(clamped.items.length, MAX_GLOBAL_DEALS);

  const fresh = normalizeGlobalDealCatalog({
    items: [deal()],
    count: 2,
    total: 240,
    page: 3,
    pageSize: 40,
    hasMore: true
  });
  assert.equal(fresh.total, 240);
  assert.equal(fresh.page, 3);
  assert.equal(fresh.pageSize, 40);
  assert.equal(fresh.hasMore, true);

  const legacy = normalizeGlobalDealCatalog({ items: [deal()], count: 1 });
  assert.equal(legacy.total, 1);
  assert.equal(legacy.page, 1);
});

test("la pagina y el tamano se acotan al contrato al normalizar", () => {
  const catalog = normalizeGlobalDealCatalog({
    items: [],
    total: 1,
    page: -4,
    pageSize: GLOBAL_DEALS_PAGE_SIZE_MAX + 100,
    hasMore: false
  });
  assert.equal(catalog.page, 1);
  assert.equal(catalog.pageSize, GLOBAL_DEALS_PAGE_SIZE_MAX);
});

test("los parametros de pagina del BFF se aceptan solo si son enteros positivos", () => {
  assert.equal(parseGlobalDealPageParam("2"), 2);
  assert.equal(parseGlobalDealPageParam("abc"), null);
  assert.equal(parseGlobalDealPageParam("0"), null);
  assert.equal(parseGlobalDealPageParam("-1"), null);
  assert.equal(parseGlobalDealPageParam("1.5"), null);
  assert.equal(parseGlobalDealPageParam(null), null);
});
