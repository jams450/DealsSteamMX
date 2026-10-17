import test from "node:test";
import assert from "node:assert/strict";
import { normalizeBundleCatalog, MAX_BUNDLES, MAX_LINKED_GAMES, MAX_TIERS } from "./bundles.ts";

function linkedGame(appId: number, name = `Game ${appId}`) {
  return { appId, name, imageUrl: null };
}

function tier(overrides: Record<string, unknown> = {}) {
  return {
    priceMinor: 1999,
    currency: "USD",
    addon: false,
    itemsComplete: true,
    itemCount: 3,
    eligibleGameCount: 2,
    allocatedPerGameMinor: 1000,
    allocationStatus: "ok",
    requiresWholeTierPurchase: true,
    ...overrides
  };
}

function bundle(overrides: Record<string, unknown> = {}) {
  return {
    source: "itad",
    bundleKey: "bundle-1",
    title: "Mega Bundle",
    shopName: "Fanatical",
    pageUrl: "https://isthereanydeal.com/bundle/x/",
    dealUrl: "https://isthereanydeal.com/bundle/x/aff",
    details: null,
    publishedAt: "2026-09-20T00:00:00Z",
    expiresAt: "2026-10-20T00:00:00Z",
    observedAt: "2026-09-21T00:00:00Z",
    linkedGames: [linkedGame(1)],
    tiers: [tier()],
    ...overrides
  };
}

test("una carga vacia o invalida es un catalogo vacio, nunca un error de render", () => {
  for (const input of [null, undefined, 7, "x", {}, { items: "no" }]) {
    const catalog = normalizeBundleCatalog(input);
    assert.deepEqual(catalog.items, []);
    assert.equal(catalog.count, 0);
    assert.equal(catalog.maxItems, MAX_BUNDLES);
  }
});

test("un bundle valido conserva enlace de afiliado, juegos ligados y reparto", () => {
  const catalog = normalizeBundleCatalog({ items: [bundle()], count: 1, maxItems: 100 });
  assert.equal(catalog.count, 1);
  assert.equal(catalog.maxItems, 100);
  const item = catalog.items[0];
  assert.equal(item.bundleKey, "bundle-1");
  assert.equal(item.dealUrl, "https://isthereanydeal.com/bundle/x/aff");
  assert.deepEqual(item.linkedGames, [{ appId: 1, name: "Game 1", imageUrl: null }]);
  assert.equal(item.tiers[0].allocationStatus, "ok");
  assert.equal(item.tiers[0].allocatedPerGameMinor, 1000);
  assert.equal(item.tiers[0].requiresWholeTierPurchase, true);
});

test("un bundle sin titulo o sin llave se descarta entero", () => {
  const catalog = normalizeBundleCatalog({
    items: [bundle({ title: "  " }), bundle({ bundleKey: null }), bundle({ source: "" }), bundle()]
  });
  assert.equal(catalog.items.length, 1);
});

test("un tier con estado de reparto desconocido se descarta, no se adivina", () => {
  const catalog = normalizeBundleCatalog({ items: [bundle({ tiers: [tier({ allocationStatus: "invented" })] })] });
  assert.deepEqual(catalog.items[0].tiers, []);
});

test("un estado ok exige una cifra de reparto valida; si falta, el tier se descarta", () => {
  const catalog = normalizeBundleCatalog({
    items: [bundle({ tiers: [tier({ allocatedPerGameMinor: null }), tier({ allocatedPerGameMinor: -5 })] })]
  });
  assert.deepEqual(catalog.items[0].tiers, []);
});

test("un estado no asignable nunca conserva una cifra de reparto", () => {
  const catalog = normalizeBundleCatalog({
    items: [bundle({ tiers: [tier({ allocationStatus: "no_tier_price", allocatedPerGameMinor: 999 })] })]
  });
  assert.equal(catalog.items[0].tiers[0].allocatedPerGameMinor, null);
  assert.equal(catalog.items[0].tiers[0].requiresWholeTierPurchase, false);
});

test("los juegos ligados invalidos se descartan y el tope se respeta", () => {
  const games = Array.from({ length: MAX_LINKED_GAMES + 5 }, (_, index) => linkedGame(index + 1));
  games.push({ appId: 0, name: "invalid" }, { appId: 2, name: "" } as never);
  const catalog = normalizeBundleCatalog({ items: [bundle({ linkedGames: games })] });
  assert.equal(catalog.items[0].linkedGames.length, MAX_LINKED_GAMES);
  assert.equal(catalog.items[0].linkedGames[0].appId, 1);
});

test("los tiers se acotan al tope del contrato", () => {
  const tiers = Array.from({ length: MAX_TIERS + 3 }, () => tier());
  const catalog = normalizeBundleCatalog({ items: [bundle({ tiers })] });
  assert.equal(catalog.items[0].tiers.length, MAX_TIERS);
});

test("los catalogos se acotan al tope del contrato", () => {
  const items = Array.from({ length: MAX_BUNDLES + 3 }, (_, index) => bundle({ bundleKey: `b-${index}` }));
  const catalog = normalizeBundleCatalog({ items });
  assert.equal(catalog.items.length, MAX_BUNDLES);
});

test("una URL que no es https absoluta se ignora en vez de llegar a un href", () => {
  const catalog = normalizeBundleCatalog({
    items: [bundle({ pageUrl: "http://x/", dealUrl: "javascript:alert(1)" })]
  });
  assert.equal(catalog.items[0].pageUrl, null);
  assert.equal(catalog.items[0].dealUrl, null);
});

test("una fecha invalida se normaliza a null", () => {
  const catalog = normalizeBundleCatalog({ items: [bundle({ publishedAt: "no-date", expiresAt: 7 })] });
  assert.equal(catalog.items[0].publishedAt, null);
  assert.equal(catalog.items[0].expiresAt, null);
});

test("el conteo del servidor se respeta y el local es el respaldo", () => {
  assert.equal(normalizeBundleCatalog({ items: [bundle()], count: 9 }).count, 9);
  assert.equal(normalizeBundleCatalog({ items: [bundle()] }).count, 1);
});
