import test from "node:test";
import assert from "node:assert/strict";
import { normalizeWishlistResponse } from "./wishlist-contract.ts";

// Los cinco sellos de proveedor son lo nuevo y lo que se rompe en silencio: un nombre que no coincida con
// el JSON de la API deja la celda en «—» para siempre y la fila parece simplemente sin sincronizar. Este
// test fija los nombres exactos de los campos, uno por uno.
const stamps = {
  steamSyncedAt: "2026-10-01T10:00:00Z",
  itadSyncedAt: "2026-10-02T10:00:00Z",
  ggDealsSyncedAt: "2026-10-03T10:00:00Z",
  epicSyncedAt: "2026-10-04T10:00:00Z",
  microsoftSyncedAt: "2026-10-05T10:00:00Z"
};

const winnerFields = [
  "bestOfficialSource", "bestOfficialLabel", "bestOfficialClassification", "bestOfficialPricingType",
  "bestKeyshopSource", "bestKeyshopLabel", "bestKeyshopClassification", "bestKeyshopPricingType"
] as const;

test("metadatos del ganador conservan fuente, tienda, clasificación y FX sin alterar importes", () => {
  const metadata = {
    bestOfficialSource: "epic", bestOfficialLabel: "Epic Games Store",
    bestOfficialClassification: "official", bestOfficialPricingType: "regional",
    bestKeyshopSource: "ggdeals", bestKeyshopLabel: "Example Keys",
    bestKeyshopClassification: "keyshop", bestKeyshopPricingType: "fx_estimate"
  };
  const parsed = normalizeWishlistResponse(envelope({ ...metadata, bestOfficialMinor: 0, bestKeyshopMinor: 123 }));
  assert.ok(parsed);
  for (const field of winnerFields) assert.equal(parsed.items[0][field], metadata[field]);
  assert.equal(parsed.items[0].bestOfficialMinor, 0);
  assert.equal(parsed.items[0].bestKeyshopMinor, 123);
});

test("payload anterior y metadatos inválidos quedan null, nunca se infiere official", () => {
  for (const metadata of [{}, Object.fromEntries(winnerFields.map(field => [field, 42]))]) {
    const parsed = normalizeWishlistResponse(envelope({ ...metadata, bestOfficialMinor: 100 }));
    assert.ok(parsed);
    for (const field of winnerFields) assert.equal(parsed.items[0][field], null);
    assert.equal(parsed.items[0].bestOfficialMinor, 100);
  }
});

test("metadatos nullable se limpian por campo sin atribuir tienda ni clasificación", () => {
  const parsed = normalizeWishlistResponse(envelope({
    bestOfficialSource: " steam ", bestOfficialLabel: " Steam ",
    bestOfficialClassification: null, bestOfficialPricingType: "regional",
    bestKeyshopSource: null, bestKeyshopLabel: " ",
    bestKeyshopClassification: null, bestKeyshopPricingType: null
  }));
  assert.ok(parsed);
  assert.equal(parsed.items[0].bestOfficialSource, "steam");
  assert.equal(parsed.items[0].bestOfficialLabel, "Steam");
  assert.equal(parsed.items[0].bestOfficialClassification, null);
  assert.equal(parsed.items[0].bestOfficialPricingType, "regional");
  for (const field of winnerFields.filter(field => field.startsWith("bestKeyshop"))) {
    assert.equal(parsed.items[0][field], null);
  }
});

test("casing de metadatos se conserva y PascalCase del DTO se acepta", () => {
  const parsed = normalizeWishlistResponse(envelope({
    BestOfficialSource: " Steam ", BestOfficialLabel: " Steam ",
    BestOfficialClassification: "Unknown", BestOfficialPricingType: "Regional",
    BestKeyshopLabel: null
  }));
  assert.ok(parsed);
  assert.equal(parsed.items[0].bestOfficialSource, "Steam");
  assert.equal(parsed.items[0].bestOfficialLabel, "Steam");
  assert.equal(parsed.items[0].bestOfficialClassification, "Unknown");
  assert.equal(parsed.items[0].bestOfficialPricingType, "Regional");
  assert.equal(parsed.items[0].bestKeyshopLabel, null);
});

const envelope = (item: Record<string, unknown>) => ({
  state: "ok",
  syncedAt: null,
  minViableDiscountPercent: 50,
  items: [{ appId: 921570, name: "OCTOPATH TRAVELER", ...item }]
});

test("cada sello de proveedor se normaliza desde su propio campo", () => {
  const parsed = normalizeWishlistResponse(envelope(stamps));
  assert.ok(parsed);
  const item = parsed.items[0];
  assert.equal(item.steamSyncedAt, "2026-10-01T10:00:00Z");
  assert.equal(item.itadSyncedAt, "2026-10-02T10:00:00Z");
  assert.equal(item.ggDealsSyncedAt, "2026-10-03T10:00:00Z");
  assert.equal(item.epicSyncedAt, "2026-10-04T10:00:00Z");
  assert.equal(item.microsoftSyncedAt, "2026-10-05T10:00:00Z");
});

test("la propiedad ownedStores admite multiples tiendas y descarta valores invalidos", () => {
  const parsed = normalizeWishlistResponse(envelope({ ownedStores: ["epic", "xbox", ""] }));
  assert.ok(parsed);
  assert.deepEqual(parsed.items[0].ownedStores, ["epic", "xbox"]);
});

test("un proveedor sin sincronizar es null y no rompe al resto de la fila", () => {
  const parsed = normalizeWishlistResponse(envelope({ ...stamps, epicSyncedAt: null }));
  assert.ok(parsed);
  assert.equal(parsed.items[0].epicSyncedAt, null);
  assert.equal(parsed.items[0].microsoftSyncedAt, "2026-10-05T10:00:00Z");
});

test("un sello ausente o inválido queda en null en vez de propagar basura", () => {
  const parsed = normalizeWishlistResponse(envelope({ steamSyncedAt: "no es fecha" }));
  assert.ok(parsed);
  assert.equal(parsed.items[0].steamSyncedAt, null);
  assert.equal(parsed.items[0].itadSyncedAt, null);
});
