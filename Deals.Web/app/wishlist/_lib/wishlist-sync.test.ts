import test from "node:test";
import assert from "node:assert/strict";
import type { WishlistItem } from "./wishlist-contract.ts";
import { SYNC_STORES, latestSyncTime, syncStamp } from "./wishlist-sync.ts";

const item = (overrides: Partial<WishlistItem> = {}): WishlistItem => ({
  appId: 921570,
  name: "OCTOPATH TRAVELER",
  imageUrl: null,
  priority: null,
  addedAt: null,
  itadGameId: "018d959e-e79a-7189-a071-bc4f582ff378",
  refreshedAt: null,
  steamSyncedAt: null,
  itadSyncedAt: null,
  ggDealsSyncedAt: null,
  epicSyncedAt: null,
  microsoftSyncedAt: null,
  basePriceMinor: null,
  baseCurrency: null,
  historyLowMinor: null,
  historyLowCurrency: null,
  bestOfficialMinor: null,
  bestKeyshopMinor: null,
  ...overrides
});

test("cada proveedor lee su propio sello, no el del vecino", () => {
  const row = item({
    steamSyncedAt: "2026-10-01T10:00:00Z",
    itadSyncedAt: "2026-10-02T10:00:00Z",
    ggDealsSyncedAt: "2026-10-03T10:00:00Z",
    epicSyncedAt: "2026-10-04T10:00:00Z",
    microsoftSyncedAt: "2026-10-05T10:00:00Z"
  });

  for (const store of SYNC_STORES) {
    assert.equal(syncStamp(row, store), row[store.field], `${store.key} debe leer ${store.field}`);
  }
  // Y el conjunto cubre los cinco proveedores, sin duplicar un campo.
  assert.deepEqual(SYNC_STORES.map((store) => store.field).sort(), [
    "epicSyncedAt",
    "ggDealsSyncedAt",
    "itadSyncedAt",
    "microsoftSyncedAt",
    "steamSyncedAt"
  ]);
});

test("un proveedor sin sello queda null y no toma prestada otra fecha", () => {
  const row = item({ microsoftSyncedAt: "2026-10-05T10:00:00Z" });
  const microsoft = SYNC_STORES.find((store) => store.key === "microsoft");
  assert.ok(microsoft);
  assert.equal(syncStamp(row, microsoft), "2026-10-05T10:00:00Z");
  assert.equal(syncStamp(row, SYNC_STORES[0]), null);
});

test("latestSyncTime: el sello más reciente de los cinco", () => {
  const now = new Date("2026-10-05T10:00:00Z").getTime();
  assert.equal(
    latestSyncTime(item({ steamSyncedAt: "2026-10-01T10:00:00Z", microsoftSyncedAt: "2026-10-05T10:00:00Z" })),
    now
  );
});

test("latestSyncTime: sin ninguna sincronización es undefined, para ordenar al final", () => {
  assert.equal(latestSyncTime(item()), undefined);
  // Una fecha ilegible no puede ganar el máximo: queda fuera, no como NaN.
  assert.equal(latestSyncTime(item({ steamSyncedAt: "no es fecha" })), undefined);
});
