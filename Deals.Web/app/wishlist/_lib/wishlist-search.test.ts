import test from "node:test";
import assert from "node:assert/strict";
import {
  WISHLIST_SEARCH_DEBOUNCE_MS,
  createWishlistSearchScheduler,
  withAppliedSearch
} from "./wishlist-search.ts";

// Reloj de mentira: el retardo se avanza a mano para que el debounce sea determinista y el test no
// dependa de temporizadores reales (que harían el resultado distinto en cada corrida).
function fakeTimer() {
  let now = 0;
  let nextId = 1;
  const pending = new Map<number, { at: number; handler: () => void }>();

  return {
    timer: {
      setTimeout(handler: () => void, timeoutMs: number) {
        const id = nextId++;
        pending.set(id, { at: now + timeoutMs, handler });
        return id;
      },
      clearTimeout(handle: number) {
        pending.delete(handle);
      }
    },
    advance(ms: number) {
      now += ms;
      const due = [...pending.entries()]
        .filter(([, task]) => task.at <= now)
        .sort((left, right) => left[1].at - right[1].at);
      for (const [id, task] of due) {
        pending.delete(id);
        task.handler();
      }
    },
    pendingCount() {
      return pending.size;
    }
  };
}

test("escribir aplica la búsqueda tras el retardo breve, una sola vez para varias teclas", () => {
  const fake = fakeTimer();
  const commits: string[] = [];
  const scheduler = createWishlistSearchScheduler({ timer: fake.timer, onCommit: (value) => commits.push(value) });

  scheduler.change("H");
  scheduler.change("Ha");
  scheduler.change("Had");
  fake.advance(WISHLIST_SEARCH_DEBOUNCE_MS - 1);
  assert.deepEqual(commits, [], "antes del retardo no se aplica nada");

  fake.advance(1);
  assert.deepEqual(commits, ["Had"], "solo se aplica la última tecla, una vez");
});

test("Enter aplica de inmediato y cancela el pendiente", () => {
  const fake = fakeTimer();
  const commits: string[] = [];
  const scheduler = createWishlistSearchScheduler({ timer: fake.timer, onCommit: (value) => commits.push(value) });

  scheduler.change("Hades");
  scheduler.submit("Hades");
  assert.deepEqual(commits, ["Hades"], "Enter no espera al retardo");
  assert.equal(fake.pendingCount(), 0, "el commit pendiente quedó cancelado");

  fake.advance(WISHLIST_SEARCH_DEBOUNCE_MS);
  assert.deepEqual(commits, ["Hades"], "no se repite el commit");
});

test("limpiar aplica vacío de inmediato y no deja pasar el borrador pendiente", () => {
  const fake = fakeTimer();
  const commits: string[] = [];
  const scheduler = createWishlistSearchScheduler({ timer: fake.timer, onCommit: (value) => commits.push(value) });

  scheduler.change("Hades");
  scheduler.clear();
  assert.deepEqual(commits, [""], "limpiar aplica vacío ya");
  assert.equal(fake.pendingCount(), 0);

  fake.advance(WISHLIST_SEARCH_DEBOUNCE_MS);
  assert.deepEqual(commits, [""], "el borrador no vuelve después de limpiar");
});

test("dispose cancela el pendiente sin aplicarlo", () => {
  const fake = fakeTimer();
  const commits: string[] = [];
  const scheduler = createWishlistSearchScheduler({ timer: fake.timer, onCommit: (value) => commits.push(value) });

  scheduler.change("Hades");
  scheduler.dispose();
  fake.advance(WISHLIST_SEARCH_DEBOUNCE_MS);
  assert.deepEqual(commits, []);
});

test("withAppliedSearch cambia solo la búsqueda y conserva los filtros aplicados, no los del borrador", () => {
  const applied = {
    search: "",
    minPrice: "10",
    maxPrice: "",
    owned: "no" as const,
    subscription: "all" as const,
    categoryIds: [3],
    uncategorized: false
  };
  // El borrador del panel tiene ediciones pendientes; el commit en vivo nunca las arrastra.
  const draft = { ...applied, search: "hades", minPrice: "999" };

  assert.deepEqual(withAppliedSearch(applied, draft.search), { ...applied, search: "hades" });
});
