import test from "node:test";
import assert from "node:assert/strict";
import { normalizeStore, storeLabel, toStoreKey } from "./stores.ts";

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

test("normalizeStore: alias de tienda y slug de plataforma abierto; lo imposible, null", () => {
  assert.equal(normalizeStore("Epic Games"), "epic");
  assert.equal(normalizeStore(" origin "), "origin"); // slug abierto: ya no es null como en toStoreKey
  assert.equal(normalizeStore("switch"), "switch"); // consola nueva, sin código
  assert.equal(normalizeStore("3DS"), "3ds");
  assert.equal(normalizeStore("psp"), "psp");
  assert.equal(normalizeStore("snes"), "snes");
  assert.equal(normalizeStore("origin"), "origin"); // slug abierto: entra como plataforma
  assert.equal(normalizeStore("origin store"), null); // el espacio rompe el slug
  assert.equal(normalizeStore("a"), null); // el slug mide 2..32
  assert.equal(normalizeStore("x".repeat(33)), null);
  assert.equal(normalizeStore(""), null);
  assert.equal(normalizeStore(7), null);
  assert.equal(normalizeStore(null), null);
});
