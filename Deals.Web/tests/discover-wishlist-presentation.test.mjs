import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
const read = (path) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("wishlist owns the freshness panel and discover retains its categories", async () => {
  const [discover, wishlist, panel] = await Promise.all([
    read("app/discover/discover-client.tsx"), read("app/wishlist/wishlist-client.tsx"),
    read("app/discover/_components/freshness-panel.tsx")
  ]);
  assert.ok(!discover.includes("CatalogFreshnessPanel"));
  assert.equal((wishlist.match(/<CatalogFreshnessPanel\s*\/>/g) ?? []).length, 1);
  assert.ok(discover.includes("DISCOVER_CATEGORIES.map"));
  assert.ok(panel.includes("CATALOG_FRESHNESS_FILTERS"));
  assert.ok(panel.includes("getCatalogFreshnessPage"));
});

test("discover highlights both native and estimated prices and enlarges the score", async () => {
  const source = await read("app/discover/_components/deal-card.tsx");
  assert.equal((source.match(/deal-price text-2xl text-\[color:var\(--color-success\)\]/g) ?? []).length, 2);
  assert.ok(source.includes('className="text-lg font-bold tabular-nums text-primary"'));
  assert.ok(source.includes("Nota ${item.score.toFixed(1)}/10"));
  assert.ok(source.includes("≈ ${formatMinor(item.mxnPriceMinor"));
  assert.ok(source.includes("Estimado"));
});
