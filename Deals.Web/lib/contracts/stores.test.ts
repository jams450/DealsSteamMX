import test from "node:test";
import assert from "node:assert/strict";
import { storeLabel, toStoreKey } from "./stores.ts";

test("toStoreKey: minúscula, alias conocidos y rechazo de lo ajeno al catálogo", () => {
  assert.equal(toStoreKey("GOG"), "gog");
  assert.equal(toStoreKey("Epic Games"), "epic");
  assert.equal(toStoreKey("ubisoft connect"), "ubisoft");
  assert.equal(toStoreKey("battle.net"), "battlenet");
  assert.equal(toStoreKey(" origin "), null);
  assert.equal(toStoreKey(""), null);
  assert.equal(toStoreKey(7), null);
  assert.equal(toStoreKey(null), null);
});

test("storeLabel: nombre visible de las llaves conocidas; una ajena se muestra tal cual", () => {
  assert.equal(storeLabel("ubisoft"), "Ubisoft Connect");
  assert.equal(storeLabel("battlenet"), "Battle.net");
  assert.equal(storeLabel("epic"), "Epic Games");
  assert.equal(storeLabel("battle.net"), "Battle.net");
  assert.equal(storeLabel("nintendo"), "nintendo");
});
