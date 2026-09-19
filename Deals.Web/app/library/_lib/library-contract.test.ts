import test from "node:test";
import assert from "node:assert/strict";
import {
  groupLibraryItems,
  normalizeLibraryImportResponse,
  normalizeLibraryResponse,
  type LibraryItem
} from "./library-contract.ts";
import type { Review } from "../../../lib/contracts/reviews.ts";

const owned = {
  userLibraryId: 7,
  store: "GOG",
  storeGameId: "1423049311",
  title: "Cyberpunk 2077",
  state: "owned",
  isInstalled: true,
  addedAt: "2024-01-05T00:00:00.1234567Z",
  importedAt: "2026-09-18T10:00:00"
};

function items(input: unknown) {
  return normalizeLibraryResponse(input)?.items ?? [];
}

test("normalizeLibraryResponse: acepta { items } y el arreglo crudo", () => {
  assert.equal(items({ items: [owned] }).length, 1);
  assert.equal(items([owned]).length, 1);
  // Un `items` que no es arreglo se rechaza: no puede leerse como "biblioteca vacía".
  assert.equal(normalizeLibraryResponse({ items: "nope" }), null);
  assert.equal(normalizeLibraryResponse(42), null);
});

test("normalizeLibraryResponse: normaliza tienda, estado y timestamp a UTC", () => {
  const [item] = items({ items: [owned] }) as LibraryItem[];
  assert.equal(item?.store, "gog");
  assert.equal(item?.state, "owned");
  assert.equal(item?.isInstalled, true);
  assert.equal(item?.addedAt, "2024-01-05T00:00:00Z");
  assert.equal(item?.importedAt, "2026-09-18T10:00:00Z");
});

test("normalizeLibraryResponse: solo `true` literal marca instalado", () => {
  const [item] = items({ items: [{ ...owned, isInstalled: "yes" }] }) as LibraryItem[];
  assert.equal(item?.isInstalled, false);
});

test("normalizeLibraryResponse: descarta estados y campos desconocidos, nunca los mapea a owned", () => {
  assert.equal(items({ items: [{ ...owned, state: "free" }] }).length, 0);
  assert.equal(items({ items: [{ ...owned, state: undefined }] }).length, 0);
  assert.equal(items({ items: [{ ...owned, title: "   " }] }).length, 0);
  assert.equal(items({ items: [{ ...owned, userLibraryId: 0 }] }).length, 0);
  assert.equal(items({ items: [{ ...owned, store: undefined }] }).length, 0);
});

test("normalizeLibraryResponse: una fecha inválida es null, la fila se queda", () => {
  const [item] = items({ items: [{ ...owned, addedAt: "2026-02-30T00:00:00Z", importedAt: "ayer" }] }) as LibraryItem[];
  assert.equal(item?.addedAt, null);
  assert.equal(item?.importedAt, null);
});

test("normalizeLibraryResponse: subscription sobrevive como subscription", () => {
  const [item] = items({ items: [{ ...owned, store: "Xbox", state: "subscription", storeGameId: "9NKX70BBCDRN" }] }) as LibraryItem[];
  assert.equal(item?.state, "subscription");
});

// --- Precios y binding (Fase 3) ---

const exact = {
  ...owned,
  priceState: "exact",
  bindingSource: "itad",
  steamAppId: 1091500,
  bestOfficialMinor: 19900,
  bestKeyshopMinor: 14900,
  historyLowMinor: 9900,
  basePriceMinor: 1999,
  baseCurrency: "USD"
};

test("precios: un priceState desconocido o ausente cae a none, nunca a exact", () => {
  assert.equal(items({ items: [{ ...owned, priceState: "confirmed" }] })[0]?.priceState, "none");
  assert.equal(items({ items: [{ ...owned }] })[0]?.priceState, "none");
  assert.equal(items({ items: [{ ...owned, priceState: "EXACT" }] })[0]?.priceState, "exact");
});

test("precios: una respuesta vieja (sin campos de precio) sigue parseando", () => {
  const [item] = items({ items: [owned] }) as LibraryItem[];
  assert.equal(item?.priceState, "none");
  assert.equal(item?.bindingSource, null);
  assert.equal(item?.steamAppId, null);
  assert.equal(item?.bestOfficialMinor, null);
  assert.equal(item?.bestKeyshopMinor, null);
  assert.equal(item?.historyLowMinor, null);
  assert.equal(item?.basePriceMinor, null);
  assert.equal(item?.baseCurrency, null);
});

test("precios: null se conserva y un importe no entero o negativo se rechaza", () => {
  const [item] = items({
    items: [{ ...exact, bestOfficialMinor: 199.5, bestKeyshopMinor: -100, historyLowMinor: "9900", basePriceMinor: null }]
  }) as LibraryItem[];
  assert.equal(item?.bestOfficialMinor, null);
  assert.equal(item?.bestKeyshopMinor, null);
  assert.equal(item?.historyLowMinor, 9900);
  assert.equal(item?.basePriceMinor, null);
});

test("precios: bindingSource inválido o moneda malformada se anulan", () => {
  const [item] = items({ items: [{ ...exact, bindingSource: "guess", baseCurrency: "usd$", steamAppId: 0 }] }) as LibraryItem[];
  assert.equal(item?.bindingSource, null);
  assert.equal(item?.baseCurrency, null);
  assert.equal(item?.steamAppId, null);
});

test("precios: title_candidate conserva sus importes", () => {
  const [item] = items({ items: [{ ...exact, priceState: "title_candidate", bindingSource: "title" }] }) as LibraryItem[];
  assert.equal(item?.priceState, "title_candidate");
  assert.equal(item?.bindingSource, "title");
  assert.equal(item?.bestOfficialMinor, 19900);
});

test("precios: una suscripción no puede llevar precio, aunque el backend lo mande", () => {
  const [item] = items({
    items: [{ ...exact, state: "subscription", store: "Xbox", priceState: "exact" }]
  }) as LibraryItem[];
  assert.equal(item?.priceState, "subscription");
  assert.equal(item?.bestOfficialMinor, null);
  assert.equal(item?.bestKeyshopMinor, null);
  assert.equal(item?.historyLowMinor, null);
  assert.equal(item?.basePriceMinor, null);
  assert.equal(item?.baseCurrency, null);
});

test("normalizeLibraryImportResponse: los cuatro contadores son obligatorios", () => {
  const report = { imported: 1, updated: 2, unresolved: 3, unsupportedSource: 4, byStore: { Steam: 1, GOG: 2 } };
  assert.deepEqual(normalizeLibraryImportResponse(report)?.byStore, [
    { store: "gog", count: 2 },
    { store: "steam", count: 1 }
  ]);
  assert.equal(normalizeLibraryImportResponse({ ...report, updated: undefined }), null);
  assert.equal(normalizeLibraryImportResponse({ ...report, imported: -1 }), null);
});

test("normalizeLibraryImportResponse: un byStore raro pierde el desglose, no el reporte", () => {
  const report = { imported: 0, updated: 0, unresolved: 0, unsupportedSource: 0, byStore: ["Steam"] };
  assert.deepEqual(normalizeLibraryImportResponse(report)?.byStore, []);
  assert.equal(normalizeLibraryImportResponse({ imported: 0, updated: 0, unresolved: 0, unsupportedSource: 0 })?.byStore.length, 0);
});

// --- Reseñas (Fase 5) ---

const review = {
  reviewId: 7,
  gameId: 100,
  platform: "gog",
  startedMonth: "2026-03",
  finishedMonth: null,
  score: 88,
  scoreLabel: "muy bueno",
  isGoty: true,
  body: "Buen juego",
  created: "2026-09-18T10:00:00Z",
  updated: null
};

test("reseñas: gameId y review ausentes degradan a null sin tumbar la fila", () => {
  const [item] = items({ items: [owned] }) as LibraryItem[];
  assert.equal(item?.gameId, null);
  assert.equal(item?.review, null);
});

test("reseñas: la reseña solo se acepta si coincide con el (gameId, platform) de la fila", () => {
  const [matching] = items({ items: [{ ...owned, gameId: 100, review }] }) as LibraryItem[];
  assert.equal(matching?.gameId, 100);
  assert.equal(matching?.review?.reviewId, 7);

  const [otherGame] = items({ items: [{ ...owned, gameId: 999, review }] }) as LibraryItem[];
  assert.equal(otherGame?.review, null);

  const [otherPlatform] = items({ items: [{ ...owned, gameId: 100, review: { ...review, platform: "epic" } }] }) as LibraryItem[];
  assert.equal(otherPlatform?.review, null);
});

// --- Portada (imageUrl) ---

test("imageUrl: solo se acepta https absoluto", () => {
  const [ok] = items({ items: [{ ...owned, imageUrl: "https://images.example.com/cover.jpg" }] }) as LibraryItem[];
  assert.equal(ok?.imageUrl, "https://images.example.com/cover.jpg");

  const [http] = items({ items: [{ ...owned, imageUrl: "http://images.example.com/cover.jpg" }] }) as LibraryItem[];
  assert.equal(http?.imageUrl, null);

  const [empty] = items({ items: [{ ...owned, imageUrl: "   " }] }) as LibraryItem[];
  assert.equal(empty?.imageUrl, null);

  const [notString] = items({ items: [{ ...owned, imageUrl: 42 }] }) as LibraryItem[];
  assert.equal(notString?.imageUrl, null);

  const [invalid] = items({ items: [{ ...owned, imageUrl: "no-es-una-url" }] }) as LibraryItem[];
  assert.equal(invalid?.imageUrl, null);
});

test("imageUrl: una suscripción sí conserva portada", () => {
  const [item] = items({
    items: [{ ...owned, state: "subscription", store: "Xbox", imageUrl: "https://images.example.com/gp.jpg" }]
  }) as LibraryItem[];
  assert.equal(item?.state, "subscription");
  assert.equal(item?.imageUrl, "https://images.example.com/gp.jpg");
});

// --- Agrupación por juego (Fase 6) ---

function row(overrides: Partial<LibraryItem> & { readonly userLibraryId: number }): LibraryItem {
  return {
    store: "gog",
    storeGameId: "1",
    title: "Juego",
    imageUrl: null,
    state: "owned",
    isInstalled: false,
    addedAt: null,
    importedAt: null,
    priceState: "none",
    bindingSource: null,
    steamAppId: null,
    gameId: null,
    review: null,
    bestOfficialMinor: null,
    bestKeyshopMinor: null,
    historyLowMinor: null,
    basePriceMinor: null,
    baseCurrency: null,
    ...overrides
  };
}

function reviewFor(overrides: Partial<Review> = {}): Review {
  return {
    reviewId: 1,
    gameId: 100,
    platform: "gog",
    startedMonth: null,
    finishedMonth: null,
    score: null,
    scoreLabel: null,
    isGoty: false,
    body: null,
    created: null,
    updated: null,
    ...overrides
  };
}

test("agrupación: el mismo gameId produce un grupo con dos plataformas", () => {
  const groups = groupLibraryItems([
    row({ userLibraryId: 1, gameId: 100, title: "Portal 2", store: "gog", imageUrl: "https://a/1.jpg", review: reviewFor() }),
    row({ userLibraryId: 2, gameId: 100, title: "Portal 2", store: "steam", state: "subscription", isInstalled: true })
  ]);

  assert.equal(groups.length, 1);
  const [game] = groups;
  assert.equal(game?.key, "game:100");
  assert.equal(game?.platforms.length, 2);
  assert.deepEqual(game?.stores, ["gog", "steam"]);
  assert.deepEqual(game?.states, ["owned", "subscription"]);
  assert.equal(game?.imageUrl, "https://a/1.jpg");
  assert.equal(game?.hasReview, true);
  assert.equal(game?.isInstalled, true);
});

test("agrupación: dos filas sin gameId son dos grupos distintos y no se fusionan", () => {
  const groups = groupLibraryItems([
    row({ userLibraryId: 7, title: "Mismo título" }),
    row({ userLibraryId: 8, title: "Mismo título" })
  ]);

  assert.equal(groups.length, 2);
  assert.deepEqual(groups.map((game) => game.key).sort(), ["row:7", "row:8"]);
  assert.ok(groups.every((game) => game.gameId === null && game.platforms.length === 1));
});

test("agrupación: lastReview usa el updated mayor; sin updated cae a created", () => {
  const older = reviewFor({ reviewId: 1, updated: "2026-01-01T00:00:00Z" });
  const newer = reviewFor({ reviewId: 2, platform: "steam", created: "2026-06-01T00:00:00Z", updated: null });

  const [bothUpdated] = groupLibraryItems([
    row({ userLibraryId: 1, gameId: 100, review: older }),
    row({ userLibraryId: 2, gameId: 100, store: "steam", review: newer })
  ]);
  assert.equal(bothUpdated?.lastReview?.reviewId, 2);

  const [fallback] = groupLibraryItems([
    row({ userLibraryId: 1, gameId: 100, review: reviewFor({ reviewId: 3, created: "2025-01-01T00:00:00Z" }) }),
    row({
      userLibraryId: 2,
      gameId: 100,
      store: "steam",
      review: reviewFor({ reviewId: 4, platform: "steam", updated: "2026-06-01T00:00:00Z" })
    })
  ]);
  assert.equal(fallback?.lastReview?.reviewId, 4);
});

test("agrupación: playedYear sale de finishedMonth y cae a startedMonth", () => {
  const [finished] = groupLibraryItems([
    row({ userLibraryId: 1, gameId: 100, review: reviewFor({ finishedMonth: "2025-11", startedMonth: "2024-03" }) })
  ]);
  assert.equal(finished?.playedYear, 2025);

  const [started] = groupLibraryItems([
    row({ userLibraryId: 1, gameId: 100, review: reviewFor({ finishedMonth: null, startedMonth: "2024-03" }) })
  ]);
  assert.equal(started?.playedYear, 2024);

  const [noMonths] = groupLibraryItems([row({ userLibraryId: 1, gameId: 100, review: reviewFor() })]);
  assert.equal(noMonths?.playedYear, null);

  const [malformed] = groupLibraryItems([
    row({
      userLibraryId: 1,
      gameId: 100,
      review: reviewFor({ finishedMonth: "2026-1" as unknown as string, startedMonth: null })
    })
  ]);
  assert.equal(malformed?.playedYear, null);
});

test("agrupación: sin reseñas hasReview y playedYear son null/false", () => {
  const [game] = groupLibraryItems([
    row({ userLibraryId: 1, gameId: 100, isInstalled: false }),
    row({ userLibraryId: 2, gameId: 100, store: "steam" })
  ]);
  assert.equal(game?.hasReview, false);
  assert.equal(game?.lastReview, null);
  assert.equal(game?.playedYear, null);
  assert.equal(game?.isInstalled, false);
});

test("agrupación: isInstalled es true si cualquier plataforma lo está", () => {
  const [game] = groupLibraryItems([
    row({ userLibraryId: 1, gameId: 100, isInstalled: false }),
    row({ userLibraryId: 2, gameId: 100, store: "steam", isInstalled: true })
  ]);
  assert.equal(game?.isInstalled, true);
});
