import test from "node:test";
import assert from "node:assert/strict";
import { isUbisoftPublisher } from "./ubisoft-search.ts";

test("Ubisoft eligibility: exact publisher, trimmed and case insensitive", () => {
  for (const publishers of [["Ubisoft"], ["  uBiSoFt \t"], ["Valve", "Ubisoft"]]) {
    assert.equal(isUbisoftPublisher(publishers), true);
  }
});

test("Ubisoft eligibility: unknown, malformed, other publishers and substring traps hidden", () => {
  for (const publishers of [undefined, null, [], "Ubisoft", {}, 7, [null, 7, {}], ["Valve"], ["Ubisoft Montreal"], ["Not Ubisoft"], ["Ubisoft Entertainment"], ["Ubisoft®"]]) {
    assert.equal(isUbisoftPublisher(publishers), false);
  }
});

test("Ubisoft eligibility: developer and ownership metadata are not publisher evidence", () => {
  assert.equal(isUbisoftPublisher({ developers: ["Ubisoft"], ownedStores: ["ubisoft"] }), false);
  assert.equal(isUbisoftPublisher([{ developer: "Ubisoft" }]), false);
});
