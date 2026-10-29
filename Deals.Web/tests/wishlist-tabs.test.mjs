import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
const read = () => readFile(new URL("../app/wishlist/wishlist-client.tsx", import.meta.url), "utf8");

test("wishlist separates games and freshness into linked accessible tabs", async () => {
  const source = await read();
  assert.ok(source.includes('role="tablist"'));
  assert.ok(source.includes('tab.id === "games" ? "tabler-badge-info" : "tabler-badge-success"'));
  assert.ok(source.includes('activeTab === tab.id ? "ring-2 ring-current"'));
  assert.ok(source.includes('role="tab"'));
  assert.ok(source.includes('aria-selected={activeTab === tab.id}'));
  assert.ok(source.includes('aria-controls={`wishlist-panel-${tab.id}`}'));
  for (const id of ["games", "freshness"]) {
    assert.ok(source.includes(`id="wishlist-panel-${id}"`));
    assert.ok(source.includes(`hidden={activeTab !== "${id}"}`));
  }
});

test("tabs keep games mounted, defer freshness until visited and support keyboard navigation", async () => {
  const source = await read();
  assert.ok(source.includes('useState<"games" | "freshness">("games")'));
  assert.ok(source.includes('{freshnessVisited ? <CatalogFreshnessPanel /> : null}'));
  assert.ok(source.includes('setFreshnessVisited(true)'));
  for (const key of ["ArrowLeft", "ArrowRight", "Home", "End"]) assert.ok(source.includes(`"${key}"`));
  assert.ok(source.includes('tabIndex={activeTab === tab.id ? 0 : -1}'));
});
