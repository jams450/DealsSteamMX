import test from "node:test";
import assert from "node:assert/strict";
import {
  MAX_PACKAGE_APP_IDS,
  exceedsPackageLimit,
  packageRequestAppIds,
  pageSelectionState,
  reconcileAppIds,
  setAppIds,
  toggleAppId
} from "./wishlist-package.ts";

test("la selección se identifica por AppID y no por posición", () => {
  const selected = toggleAppId(new Set<number>(), 1057800, true);
  assert.deepEqual(packageRequestAppIds(selected), [1057800]);

  // Reordenar o paginar no cambia la selección: no hay índice que se pueda correr.
  const afterOtherToggle = toggleAppId(selected, 292030, true);
  assert.deepEqual(packageRequestAppIds(afterOtherToggle), [292030, 1057800]);
});

test("desmarcar quita el AppID y no toca el resto", () => {
  const selected = setAppIds(new Set<number>(), [1, 2, 3], true);
  const next = toggleAppId(selected, 2, false);
  assert.deepEqual(packageRequestAppIds(next), [1, 3]);
});

test("el lote de la página marca y desmarca solo sus AppIDs", () => {
  const selected = setAppIds(new Set<number>([9]), [1, 2], true);
  assert.deepEqual(packageRequestAppIds(selected), [1, 2, 9]);

  const cleared = setAppIds(selected, [1, 2], false);
  assert.deepEqual(packageRequestAppIds(cleared), [9]);
});

test("el checkbox de cabecera distingue todo, parte y nada", () => {
  const page = [10, 20, 30];
  assert.equal(pageSelectionState(new Set(), page), "none");
  assert.equal(pageSelectionState(new Set([10]), page), "some");
  assert.equal(pageSelectionState(new Set([10, 20, 30]), page), "all");
  // Marcar juegos de otra página no completa esta.
  assert.equal(pageSelectionState(new Set([10, 20, 30, 40]), page), "all");
});

test("una página vacía no se reporta como todo seleccionado", () => {
  assert.equal(pageSelectionState(new Set([1, 2]), []), "none");
});

test("la poda devuelve la misma referencia cuando no hay nada que quitar", () => {
  const selected = new Set([1, 2]);
  const result = reconcileAppIds(selected, new Set([1, 2, 3]));
  assert.equal(result.selection, selected);
  assert.deepEqual(result.removed, []);
});

test("un juego que ya no está en la lista sale de la selección y se reporta", () => {
  const result = reconcileAppIds(new Set([1, 2, 3]), new Set([1, 3]));
  assert.deepEqual(packageRequestAppIds(result.selection), [1, 3]);
  assert.deepEqual(result.removed, [2]);
});

test("el tope del paquete es el mismo número que impone el backend", () => {
  assert.equal(MAX_PACKAGE_APP_IDS, 200);
  const atLimit = new Set(Array.from({ length: MAX_PACKAGE_APP_IDS }, (_, index) => index + 1));
  assert.equal(exceedsPackageLimit(atLimit), false);
  assert.equal(exceedsPackageLimit(new Set([...atLimit, MAX_PACKAGE_APP_IDS + 1])), true);
});
