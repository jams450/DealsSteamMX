/**
 * Reproducción pública (sin navegador) del defecto del buscador de la wishlist y verificación del
 * arreglo. Corre con:
 *
 *   node --experimental-strip-types checks/search-public/wishlist-search-repro.mjs
 *
 * Modela la interacción del usuario (teclas y tiempo) a través de la misma lógica pura que usa el
 * componente, con un reloj de mentira. El caso de control reproduce el cableado defectuoso —la toolbar
 * escribía el borrador y la petición leía los filtros aplicados— y el segundo caso corre la búsqueda
 * «mientras escribes» ya arreglada.
 */
import assert from "node:assert/strict";
import {
  WISHLIST_SEARCH_DEBOUNCE_MS,
  createWishlistSearchScheduler,
  withAppliedSearch
} from "../../Deals.Web/app/wishlist/_lib/wishlist-search.ts";

function fakeClock() {
  let now = 0;
  let nextId = 1;
  const pending = new Map();
  return {
    timer: {
      setTimeout(handler, timeoutMs) {
        const id = nextId++;
        pending.set(id, { at: now + timeoutMs, handler });
        return id;
      },
      clearTimeout(handle) {
        pending.delete(handle);
      }
    },
    advance(ms) {
      now += ms;
      const due = [...pending.entries()]
        .filter(([, task]) => task.at <= now)
        .sort((left, right) => left[1].at - right[1].at);
      for (const [id, task] of due) {
        pending.delete(id);
        task.handler();
      }
    }
  };
}

const KEYSTROKES = ["H", "Ha", "Had", "Hade", "Hades"];
// Filtros ya aplicados (los que la petición lee) y una edición pendiente del panel que NO debe viajar.
const APPLIED = {
  search: "",
  minPrice: "10",
  maxPrice: "",
  owned: "no",
  subscription: "all",
  categoryIds: [3],
  uncategorized: false
};
const PENDING_DRAFT_EDIT = { ...APPLIED, search: "Hades", minPrice: "999" };

// Caso de control: cableado defectuoso. La toolbar solo escribía el borrador; la consulta enviada leía
// `appliedFilters`, así que teclear no cambiaba nada. Debe reproducir cinco consultas sin búsqueda.
const legacyDraft = { ...APPLIED };
const legacyBackend = [];
for (const key of KEYSTROKES) {
  legacyDraft.search = key; // la toolbar escribía el borrador...
  legacyBackend.push(APPLIED.search); // ...y la petición leía los filtros aplicados
}
assert.equal(legacyDraft.search, "Hades", "el borrador sí tenía el texto");

// Arreglo: la búsqueda aplica tras el retardo breve, y el commit solo toca `search`.
const clock = fakeClock();
let fixedApplied = { ...APPLIED };
const fixedBackend = [];
const scheduler = createWishlistSearchScheduler({
  timer: clock.timer,
  onCommit: (value) => {
    fixedApplied = withAppliedSearch(fixedApplied, value);
    fixedBackend.push(fixedApplied);
  }
});
for (const key of KEYSTROKES) scheduler.change(key);
clock.advance(WISHLIST_SEARCH_DEBOUNCE_MS);

console.log("Defecto (toolbar → borrador, petición → aplicado):", JSON.stringify(legacyBackend));
console.log("Arreglo (búsqueda al escribir, con retardo):", JSON.stringify(fixedBackend.map((query) => query.search)));

assert.deepEqual(legacyBackend, ["", "", "", "", ""], "el caso de control no aplica ninguna búsqueda");
assert.deepEqual(fixedBackend.map((query) => query.search), ["Hades"], "el arreglo aplica la última tecla una vez");
assert.deepEqual(
  { ...fixedBackend[0], search: "" },
  APPLIED,
  "el commit en vivo conserva los filtros aplicados y no arrastra la edición pendiente del panel"
);
assert.notDeepEqual(
  fixedApplied,
  { ...PENDING_DRAFT_EDIT, search: "Hades" },
  "la edición pendiente minPrice=999 no viajó con la búsqueda en vivo"
);

// Enter y limpiar aplican de inmediato, sin esperar al retardo.
const immediate = [];
const immediateClock = fakeClock();
const immediateScheduler = createWishlistSearchScheduler({
  timer: immediateClock.timer,
  onCommit: (value) => immediate.push(value)
});
immediateScheduler.change("Hades");
immediateScheduler.submit("Hades");
immediateScheduler.change("Otro");
immediateScheduler.clear();
assert.deepEqual(immediate, ["Hades", ""], "Enter y limpiar aplican de inmediato y cancelan lo pendiente");

console.log("OK: defecto reproducido y arreglo verificado.");
