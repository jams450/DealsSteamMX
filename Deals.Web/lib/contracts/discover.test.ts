// Contrato de /discover (spec S1/S2/S3/S6, tarea T4). Estas pruebas fijan las dos reglas que la página no
// puede romper: (1) el sobre trae los tres contadores junto con la página seleccionada, y (2) la tarjeta
// pinta la oferta que la calificó con SU etiqueta, SU precio y SU enlace, sin inventar un appid de Steam ni
// un historico. Se ejecuta con `node --test lib/contracts/discover.test.ts` (sin instalar nada).
import test from "node:test";
import assert from "node:assert/strict";
import {
  DISCOVER_CATEGORIES,
  DISCOVER_PAGE_MAX,
  DISCOVER_PAGE_SIZE_DEFAULT,
  DISCOVER_PAGE_SIZE_MAX,
  MAX_DISCOVER_ITEMS,
  discoverCategoryCount,
  isDiscoverCategory,
  normalizeDiscoverPage,
  parseDiscoverCategoryParam,
  parseDiscoverPageParam
} from "./discover.ts";

function item(overrides: Record<string, unknown> = {}) {
  return {
    gameId: 1057800,
    steamAppId: 1057800,
    title: "Floppy Knights",
    imageUrl: "https://cdn.cloudflare.steamstatic.com/steam/apps/1057800/header.jpg",
    source: "itad",
    shopName: "GreenManGaming",
    classification: "authorized",
    pricingType: "fx_estimate",
    mxnPriceMinor: 1656,
    originalPriceMinor: 99,
    originalCurrency: "USD",
    usesFxEstimate: true,
    historyLowMinor: 1656,
    historyLowCurrency: "USD",
    dealUrl: "https://www.greenmangaming.com/games/floppy-knights?ref=aff",
    score: 9.4,
    reasons: ["price", "score"],
    observedAt: "2026-10-17T03:00:00Z",
    ...overrides
  };
}

function page(overrides: Record<string, unknown> = {}) {
  return {
    category: "under50",
    items: [item()],
    count: 1,
    total: 42,
    under50Total: 42,
    historicTotal: 7,
    scoreTotal: 3,
    page: 1,
    pageSize: 20,
    hasMore: true,
    ...overrides
  };
}

test("una carga vacia o invalida es una pagina vacia, nunca un error de render", () => {
  for (const input of [null, undefined, 7, "x", {}, { items: "no" }]) {
    const result = normalizeDiscoverPage(input, "under50");
    assert.deepEqual(result.items, []);
    assert.equal(result.count, 0);
    assert.equal(result.total, 0);
    assert.equal(result.under50Total, 0);
    assert.equal(result.historicTotal, 0);
    assert.equal(result.scoreTotal, 0);
    assert.equal(result.category, "under50");
    assert.equal(result.page, 1);
    assert.equal(result.pageSize, DISCOVER_PAGE_SIZE_DEFAULT);
    assert.equal(result.hasMore, false);
  }
});

test("una tarjeta valida conserva la identidad y la oferta de la MISMA fila", () => {
  const result = normalizeDiscoverPage(page(), "under50");
  const card = result.items[0];

  assert.equal(card.gameId, 1057800);
  assert.equal(card.steamAppId, 1057800);
  assert.equal(card.title, "Floppy Knights");
  assert.equal(card.imageUrl, "https://cdn.cloudflare.steamstatic.com/steam/apps/1057800/header.jpg");
  // La etiqueta y el enlace son los de la oferta que calificó, no los de Steam ni los del "mejor precio global".
  assert.equal(card.shopName, "GreenManGaming");
  assert.equal(card.source, "itad");
  assert.equal(card.classification, "authorized");
  assert.equal(card.pricingType, "fx_estimate");
  assert.equal(card.mxnPriceMinor, 1656);
  assert.equal(card.originalPriceMinor, 99);
  assert.equal(card.originalCurrency, "USD");
  assert.equal(card.dealUrl, "https://www.greenmangaming.com/games/floppy-knights?ref=aff");
  assert.equal(card.historyLowMinor, 1656);
  assert.equal(card.historyLowCurrency, "USD");
  assert.equal(card.score, 9.4);
  assert.deepEqual([...card.reasons], ["price", "score"]);
  assert.equal(card.observedAt, "2026-10-17T03:00:00Z");
});

test("una oferta sin Steam conserva steamAppId nulo: nunca se inventa un appid", () => {
  for (const steamAppId of [null, undefined, 0, -1, 1.5, "1057800", true]) {
    const result = normalizeDiscoverPage(page({ items: [item({ steamAppId })] }), "under50");
    assert.equal(result.items.length, 1);
    assert.equal(result.items[0].steamAppId, null);
  }

  // Un juego con Steam sí lo conserva, y no se deriva del gameId canónico.
  const withSteam = normalizeDiscoverPage(page({ items: [item({ gameId: 7, steamAppId: 620 })] }), "under50");
  assert.equal(withSteam.items[0].gameId, 7);
  assert.equal(withSteam.items[0].steamAppId, 620);
});

test("un precio estimado se etiqueta como estimado y uno regional nunca lo parece", () => {
  const estimated = normalizeDiscoverPage(page(), "under50").items[0];
  assert.equal(estimated.usesFxEstimate, true);

  // El tipo de cambio se deriva de pricingType, no del booleano del payload: una fila regional no puede
  // pintar el `≈` aunque el payload lo afirme.
  const regional = normalizeDiscoverPage(
    page({ items: [item({ pricingType: "regional", usesFxEstimate: true, originalCurrency: "MXN" })] }),
    "under50"
  ).items[0];
  assert.equal(regional.pricingType, "regional");
  assert.equal(regional.usesFxEstimate, false);

  const lyingEstimate = normalizeDiscoverPage(page({ items: [item({ usesFxEstimate: false })] }), "under50").items[0];
  assert.equal(lyingEstimate.usesFxEstimate, true);
});

test("un precio no positivo o invalido descarta la tarjeta entera", () => {
  for (const mxnPriceMinor of [0, -1, -1656, 1.5, "1656", Number.NaN, null, undefined]) {
    const result = normalizeDiscoverPage(page({ items: [item({ mxnPriceMinor })] }), "under50");
    assert.deepEqual(result.items, []);
    assert.equal(result.count, 0);
  }
});

test("un tipo de precio desconocido o una moneda ausente descartan la tarjeta", () => {
  for (const overrides of [
    { pricingType: "unconverted" },
    { pricingType: "free" },
    { pricingType: null },
    { originalCurrency: null },
    { originalCurrency: "PESOS" },
    { title: "   " },
    { shopName: null },
    { source: "" },
    { gameId: 0 }
  ]) {
    const result = normalizeDiscoverPage(page({ items: [item(overrides)] }), "under50");
    assert.deepEqual(result.items, [], JSON.stringify(overrides));
  }
});

test("el minimo historico solo cuenta con su monto y su moneda validos", () => {
  const ok = normalizeDiscoverPage(page(), "historic").items[0];
  assert.equal(ok.historyLowMinor, 1656);
  assert.equal(ok.historyLowCurrency, "USD");

  const noLow = normalizeDiscoverPage(
    page({ items: [item({ historyLowMinor: null, historyLowCurrency: "USD" })] }),
    "historic"
  ).items[0];
  assert.equal(noLow.historyLowMinor, null);
  assert.equal(noLow.historyLowCurrency, null);

  const noCurrency = normalizeDiscoverPage(
    page({ items: [item({ historyLowCurrency: null })] }),
    "historic"
  ).items[0];
  assert.equal(noCurrency.historyLowMinor, null);
  assert.equal(noCurrency.historyLowCurrency, null);

  for (const historyLowMinor of [0, -5, 1.5, Number.NaN, "1656"]) {
    const bad = normalizeDiscoverPage(page({ items: [item({ historyLowMinor })] }), "historic").items[0];
    assert.equal(bad.historyLowMinor, null);
  }
});

test("un enlace externo solo se acepta si es https absoluto", () => {
  for (const dealUrl of [
    "http://www.greenmangaming.com/deal",
    "javascript:alert(1)",
    "data:text/html,<script>",
    "//www.greenmangaming.com/deal",
    "/games/1057800",
    "no-es-una-url",
    null,
    7
  ]) {
    const result = normalizeDiscoverPage(page({ items: [item({ dealUrl })] }), "under50");
    assert.equal(result.items[0].dealUrl, null, String(dealUrl));
  }

  const kept = normalizeDiscoverPage(
    page({ items: [item({ dealUrl: "https://gg.deals/game/floppy-knights/?ref=aff&a=1" })] }),
    "under50"
  ).items[0];
  // Se conserva tal cual: reescribir la URL rompería el tag de afiliado del proveedor.
  assert.equal(kept.dealUrl, "https://gg.deals/game/floppy-knights/?ref=aff&a=1");
});

test("una portada no https no llega al render", () => {
  const result = normalizeDiscoverPage(
    page({ items: [item({ imageUrl: "http://cdn.example.com/a.jpg" })] }),
    "under50"
  ).items[0];
  assert.equal(result.imageUrl, null);
});

test("los conteos invalidos se acotan y nunca son negativos", () => {
  for (const bad of [-1, -42, 1.5, Number.NaN, "7", null, undefined, Number.POSITIVE_INFINITY]) {
    const result = normalizeDiscoverPage(
      page({ total: bad, under50Total: bad, historicTotal: bad, scoreTotal: bad, page: bad, pageSize: bad, count: bad }),
      "under50"
    );
    for (const count of [result.total, result.under50Total, result.historicTotal, result.scoreTotal]) {
      assert.equal(Number.isSafeInteger(count), true, `count not integer: ${String(count)}`);
      assert.ok(count >= 0, `count negative: ${count}`);
    }
    assert.equal(result.count, result.items.length);
    assert.ok(result.total >= result.count);
    assert.equal(result.page, 1);
    assert.equal(result.pageSize, DISCOVER_PAGE_SIZE_DEFAULT);
  }

  // Un contador del payload no puede ser menor que las tarjetas realmente pintadas.
  const lying = normalizeDiscoverPage(page({ total: 0 }), "under50");
  assert.equal(lying.total, 1);
});

test("una pagina valida conserva sus contadores y se acota al contrato", () => {
  const full = normalizeDiscoverPage(
    page({ total: 240, under50Total: 240, historicTotal: 31, scoreTotal: 9, page: 3, pageSize: 40 }),
    "under50"
  );
  assert.equal(full.total, 240);
  assert.equal(full.under50Total, 240);
  assert.equal(full.historicTotal, 31);
  assert.equal(full.scoreTotal, 9);
  assert.equal(full.page, 3);
  assert.equal(full.pageSize, 40);
  assert.equal(full.hasMore, true);

  const clamped = normalizeDiscoverPage(
    page({ page: DISCOVER_PAGE_MAX + 1, pageSize: DISCOVER_PAGE_SIZE_MAX + 10 }),
    "under50"
  );
  assert.equal(clamped.page, DISCOVER_PAGE_MAX);
  assert.equal(clamped.pageSize, DISCOVER_PAGE_SIZE_MAX);

  // `hasMore` solo con el literal true: la cadena "true" no habilita «Siguiente».
  assert.equal(normalizeDiscoverPage(page({ hasMore: "true" }), "under50").hasMore, false);
});

test("la categoria de la respuesta no puede renombrar la pestaña solicitada", () => {
  // Una respuesta que dice `score` mientras se pidió `under50` no puede etiquetar las tarjetas como nota.
  for (const category of ["score", "historic", "desconocida", null]) {
    const result = normalizeDiscoverPage(page({ category }), "under50");
    assert.equal(result.category, "under50");
  }

  const requested = normalizeDiscoverPage(page({ category: "historic" }), "historic");
  assert.equal(requested.category, "historic");

  for (const category of DISCOVER_CATEGORIES) assert.equal(isDiscoverCategory(category), true);
  for (const category of ["discount", "recent", "UNDER50", "under-50", "", null, 7]) {
    assert.equal(isDiscoverCategory(category), false);
  }
});

test("una oferta repetida del mismo juego pinta una sola tarjeta y conserva el orden", () => {
  const result = normalizeDiscoverPage(
    page({
      items: [
        item({ gameId: 3, title: "Tercero" }),
        item({ gameId: 1, title: "Primero" }),
        item({ gameId: 3, title: "Tercero repetido", shopName: "Otra tienda" }),
        item({ gameId: 2, title: "Segundo" })
      ]
    }),
    "under50"
  );

  assert.deepEqual(
    result.items.map((card) => card.gameId),
    [3, 1, 2]
  );
  assert.equal(result.items[0].title, "Tercero");
  assert.equal(result.count, 3);
});

test("los motivos son codigos conocidos y el score solo es una nota valida", () => {
  const result = normalizeDiscoverPage(
    page({ items: [item({ reasons: ["price", "price", "inventado", "history_low", 7], score: "9.4" })] }),
    "under50"
  ).items[0];
  assert.deepEqual([...result.reasons], ["price", "history_low"]);
  assert.equal(result.score, null);

  for (const score of [-1, 10.1, 42, Number.NaN, Number.POSITIVE_INFINITY]) {
    assert.equal(normalizeDiscoverPage(page({ items: [item({ score })] }), "score").items[0].score, null);
  }

  const bound = normalizeDiscoverPage(page({ items: [item({ score: 7 })] }), "score").items[0];
  assert.equal(bound.score, 7);

  const missing = normalizeDiscoverPage(page({ items: [item({ reasons: null })] }), "under50").items[0];
  assert.deepEqual([...missing.reasons], []);
});

test("una fecha de observacion invalida queda nula", () => {
  for (const observedAt of ["ayer", "2026-13-45T99:99:99Z", "", null, 17]) {
    const result = normalizeDiscoverPage(page({ items: [item({ observedAt })] }), "under50").items[0];
    assert.equal(result.observedAt, null);
  }
});

test("una pagina jamas materializa mas tarjetas que el tope del contrato", () => {
  const many = Array.from({ length: MAX_DISCOVER_ITEMS + 25 }, (_, index) => item({ gameId: index + 1 }));
  const result = normalizeDiscoverPage(page({ items: many, count: many.length, total: many.length }), "under50");
  assert.equal(result.items.length, MAX_DISCOVER_ITEMS);
  assert.equal(result.count, MAX_DISCOVER_ITEMS);
});

test("los contadores de las pestañas salen del mismo sobre, sin pedidos independientes", () => {
  const result = normalizeDiscoverPage(page({ under50Total: 12, historicTotal: 4, scoreTotal: 0 }), "under50");
  assert.equal(discoverCategoryCount(result, "under50"), 12);
  assert.equal(discoverCategoryCount(result, "historic"), 4);
  assert.equal(discoverCategoryCount(result, "score"), 0);
  assert.equal(discoverCategoryCount(null, "under50"), 0);
});

test("los parametros del BFF se aceptan solo si son validos", () => {
  assert.equal(parseDiscoverCategoryParam("under50"), "under50");
  assert.equal(parseDiscoverCategoryParam(" historic "), "historic");
  assert.equal(parseDiscoverCategoryParam("SCORE"), "score");
  for (const value of ["discount", "recent", "", null, "  "]) {
    assert.equal(parseDiscoverCategoryParam(value), null, String(value));
  }

  assert.equal(parseDiscoverPageParam("2"), 2);
  assert.equal(parseDiscoverPageParam(String(DISCOVER_PAGE_MAX)), DISCOVER_PAGE_MAX);
  for (const value of ["abc", "0", "-1", "1.5", "", null, String(DISCOVER_PAGE_MAX + 1)]) {
    assert.equal(parseDiscoverPageParam(value), null, String(value));
  }

  assert.equal(DISCOVER_PAGE_SIZE_DEFAULT, 20);
  assert.equal(DISCOVER_PAGE_SIZE_MAX, 50);
});
