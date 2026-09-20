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
