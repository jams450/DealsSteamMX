import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const read = (path) => readFile(new URL(`../${path}`, import.meta.url), "utf8");

test("discover matches wishlist responsive column counts without changing page size", async () => {
  const [discover, wishlist] = await Promise.all([
    read("app/discover/discover-client.tsx"), read("app/wishlist/wishlist-client.tsx")
  ]);
  for (const columns of ["md:grid-cols-2", "xl:grid-cols-4", "2xl:grid-cols-5"]) {
    assert.ok(discover.includes(columns));
    assert.ok(wishlist.includes(columns));
  }
  assert.ok(discover.includes("useState(DISCOVER_PAGE_SIZE_DEFAULT)"));
});

test("retired deals URL redirects to discover instead of mounting the dead feed", async () => {
  const source = await read("app/deals/page.tsx");
  assert.ok(source.includes('redirect("/discover")'));
  assert.doesNotMatch(source, /DealsClient|ProductShell|getGlobalDeals/);
});
