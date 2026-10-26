// Contrato del panel «Estado de actualización de precios» (`GET /api/bff/discover/freshness`, spec S4/S7/S9, tarea T3).
// Fija las reglas que el panel no puede romper:
//  1. el filtro pedido (`all | recent | stale | missing`) es la ÚNICA fuente de la etiqueta de la página, y sus
//     cuatro contadores llegan en el mismo sobre (nunca un pedido por pestaña);
//  2. un contador o una fecha que no se pueden leer son DESCONOCIDOS (`null`), nunca un 0 ni una fecha
//     inventada: «antiguo» no es «erróneo» y «sin precio comparable» no prueba que la consulta falló;
//  3. `recent`/`stale` exigen una fecha de precio válida; una fecha futura no cuenta como evidencia;
//  4. la fecha de precio de un proveedor y la de su última consulta son hechos distintos y se conservan por
//     separado (frescura parcial visible), y la paginación se acota al contrato.
// Se ejecuta con `node --test lib/contracts/catalog-freshness.test.ts` (sin instalar nada).
import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import {
  CATALOG_FRESHNESS_FILTERS,
  CATALOG_FRESHNESS_HOURS,
  CATALOG_FRESHNESS_PAGE_MAX,
  CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT,
  CATALOG_FRESHNESS_PAGE_SIZE_MAX,
  MAX_CATALOG_FRESHNESS_ITEMS,
  MAX_CHOSEN_PRICE_MINOR,
  MAX_CHOSEN_SHOP_NAME,
  MAX_FRESHNESS_PROVIDERS,
  catalogFreshnessCount,
  chosenOfferStoreLabel,
  formatChosenOfferPrice,
  freshnessCountFromCounts,
  freshnessShortStatusLabel,
  hasUnresolvedWishlistEntries,
  isCatalogFreshnessFilter,
  normalizeCatalogFreshnessPage,
  parseCatalogFreshnessFilterParam,
  parseCatalogFreshnessPageParam,
  unresolvedWishlistEntriesCount
} from "./catalog-freshness.ts";
import { readUpstreamJson } from "../../app/api/bff/discover/freshness/read-upstream-body.ts";

const AS_OF = "2026-10-26T12:00:00Z";

function provider(overrides: Record<string, unknown> = {}) {
  return {
    provider: "itad",
    priceObservedAtUtc: "2026-10-25T08:00:00Z",
    lastCheckedAtUtc: "2026-10-26T06:30:00Z",
    ...overrides
  };
}

function item(overrides: Record<string, unknown> = {}) {
  return {
    gameId: 1057800,
    name: "Floppy Knights",
    imageUrl: "https://cdn.cloudflare.steamstatic.com/steam/apps/1057800/header.jpg",
    steamAppId: 1057800,
    status: "recent",
    priceObservedAtUtc: "2026-10-25T08:00:00Z",
    // Campos ADITIVOS (S9): el precio, su tienda, su fuente y su tipo son de la MISMA fila ganadora, y la fecha
    // de arriba es la de esa fila, no la del precio más reciente entre todas las tiendas.
    chosenPriceMinor: 3000,
    chosenShopName: "GG.deals",
    chosenSource: "ggdeals",
    chosenPricingType: "regional",
    providers: [provider()],
    ...overrides
  };
}

function page(overrides: Record<string, unknown> = {}) {
  return {
    asOfUtc: AS_OF,
    freshnessHours: 72,
    page: 1,
    pageSize: 20,
    total: 41,
    counts: { total: 120, recent: 30, stale: 70, missing: 20 },
    items: [item()],
    ...overrides
  };
}

test("una carga vacia o invalida no inventa filas ni contadores", () => {
  for (const input of [null, undefined, 7, "x", {}, { items: "no" }, { counts: "no" }]) {
    const result = normalizeCatalogFreshnessPage(input, "all");
    assert.equal(result.filter, "all");
    assert.equal(result.asOfUtc, null);
    assert.equal(result.freshnessHours, CATALOG_FRESHNESS_HOURS);
    assert.deepEqual([...result.items], []);
    assert.equal(result.count, 0);
    assert.equal(result.total, 0);
    // Un contador ausente es desconocido, no 0: el panel pinta «—» en vez de afirmar que no hay juegos.
    assert.deepEqual({ ...result.counts }, { total: null, recent: null, stale: null, missing: null });
    assert.equal(result.page, 1);
    assert.equal(result.pageSize, CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT);
    assert.equal(result.hasMore, false);
  }
});

test("el sobre valido conserva la fecha de corte, las horas, los conteos y la pagina", () => {
  const result = normalizeCatalogFreshnessPage(page(), "all");

  assert.equal(result.filter, "all");
  assert.equal(result.asOfUtc, AS_OF);
  assert.equal(result.freshnessHours, 72);
  assert.equal(result.page, 1);
  assert.equal(result.pageSize, 20);
  assert.equal(result.total, 41);
  assert.deepEqual({ ...result.counts }, { total: 120, recent: 30, stale: 70, missing: 20 });
  assert.equal(result.count, 1);
  // 1 * 20 < 41: quedan páginas, y eso se deriva del contrato (el sobre no envía `hasMore`).
  assert.equal(result.hasMore, true);
});

test("una fila sin identidad o con un estado desconocido se descarta", () => {
  for (const overrides of [
    { gameId: null },
    { gameId: 0 },
    { gameId: -3 },
    { gameId: 1.5 },
    { gameId: "1057800" },
    { name: null },
    { name: "   " },
    { status: undefined },
    { status: "unknown" },
    { status: "fresh" }
  ]) {
    const result = normalizeCatalogFreshnessPage(page({ items: [item(overrides), item({ gameId: 7 })] }), "all");
    assert.equal(result.count, 1, JSON.stringify(overrides));
    assert.equal(result.items[0].gameId, 7);
  }

  // Un nombre desmedido se acota en vez de reventar el layout.
  const longName = normalizeCatalogFreshnessPage(page({ items: [item({ name: "x".repeat(900) })] }), "all");
  assert.equal(longName.items[0].name.length, 512);
});

test("recent y stale exigen una fecha de precio valida; missing no la exhibe", () => {
  // Sin fecha (o con una ilegible) no hay evidencia para afirmar ni frescura ni antigüedad.
  for (const priceObservedAtUtc of [null, undefined, "", "ayer", "2026-10-25", 1729000000]) {
    for (const status of ["recent", "stale"]) {
      const result = normalizeCatalogFreshnessPage(page({ items: [item({ status, priceObservedAtUtc })] }), "all");
      assert.equal(result.count, 0, `${status}/${String(priceObservedAtUtc)}`);
    }
  }

  // Un juego sin precio comparable conserva su fila, pero nunca una fecha de precio: sería una contradicción
  // («no tiene precio» y «su precio se observó el día X» no pueden ser ciertas a la vez).
  const missing = normalizeCatalogFreshnessPage(
    page({
      items: [
        item({
          status: "missing",
          priceObservedAtUtc: "2026-10-01T00:00:00Z",
          providers: [provider({ priceObservedAtUtc: null })]
        })
      ]
    }),
    "all"
  );
  assert.equal(missing.count, 1);
  assert.equal(missing.items[0].status, "missing");
  assert.equal(missing.items[0].priceObservedAtUtc, null);
});

test("una fecha futura no cuenta como evidencia de frescura", () => {
  // `asOf` es 2026-10-26T12:00Z; se toleran 5 minutos de desfase de relojes.
  const future = normalizeCatalogFreshnessPage(
    page({ items: [item({ priceObservedAtUtc: "2026-10-27T12:00:00Z" })] }),
    "all"
  );
  assert.equal(future.count, 0);

  const skew = normalizeCatalogFreshnessPage(
    page({ items: [item({ priceObservedAtUtc: "2026-10-26T12:02:00Z" })] }),
    "all"
  );
  assert.equal(skew.count, 1);
  assert.equal(skew.items[0].priceObservedAtUtc, "2026-10-26T12:02:00Z");

  // En las fechas por proveedor una fecha futura se degrada a desconocida, pero el proveedor sigue listado.
  const providerFuture = normalizeCatalogFreshnessPage(
    page({ items: [item({ providers: [provider({ priceObservedAtUtc: "2027-01-01T00:00:00Z" })] })] }),
    "all"
  );
  assert.equal(providerFuture.items[0].providers[0].priceObservedAtUtc, null);
  assert.equal(providerFuture.items[0].providers[0].provider, "itad");

  // Sin un `asOfUtc` legible no hay contra qué comparar: la fila no se descarta por una fecha futura.
  const noAsOf = normalizeCatalogFreshnessPage(
    page({ asOfUtc: "no-es-fecha", items: [item({ priceObservedAtUtc: "2030-01-01T00:00:00Z" })] }),
    "all"
  );
  assert.equal(noAsOf.asOfUtc, null);
  assert.equal(noAsOf.count, 1);
});

test("una fecha solo se acepta en ISO con hora y zona", () => {
  for (const value of ["2026-10-25", "2026-10-25T08:00", "25/10/2026", "", 1729000000, true]) {
    const result = normalizeCatalogFreshnessPage(
      page({ items: [item({ providers: [provider({ priceObservedAtUtc: value, lastCheckedAtUtc: value })] })] }),
      "all"
    );
    assert.equal(result.items[0].providers[0].priceObservedAtUtc, null, String(value));
    assert.equal(result.items[0].providers[0].lastCheckedAtUtc, null, String(value));
  }

  // Una fecha con zona distinta de Z se conserva tal cual: el render la formatea, no la reescribe.
  const offset = normalizeCatalogFreshnessPage(
    page({ items: [item({ providers: [provider({ lastCheckedAtUtc: "2026-10-25T20:30:00-06:00" })] })] }),
    "all"
  );
  assert.equal(offset.items[0].providers[0].lastCheckedAtUtc, "2026-10-25T20:30:00-06:00");
});

test("los proveedores se deduplican, se acotan y los ilegibles se descartan", () => {
  const result = normalizeCatalogFreshnessPage(
    page({
      items: [
        item({
          providers: [
            provider({ provider: "itad" }),
            provider({ provider: "itad", priceObservedAtUtc: "2026-10-24T00:00:00Z" }),
            { provider: null },
            { priceObservedAtUtc: "2026-10-25T00:00:00Z" },
            null,
            "itad",
            provider({ provider: "  ggdeals  " }),
            ...Array.from({ length: MAX_FRESHNESS_PROVIDERS + 4 }, (_, index) =>
              provider({ provider: `extra-${index}` })
            )
          ]
        })
      ]
    }),
    "all"
  );

  const providers = result.items[0].providers;
  assert.deepEqual(
    providers.map((entry) => entry.provider),
    [
      "itad",
      "ggdeals",
      ...Array.from({ length: MAX_FRESHNESS_PROVIDERS - 2 }, (_, index) => `extra-${index}`)
    ]
  );
  // El primer duplicado gana: no se reordena ni se promedia una fecha con otra.
  assert.equal(providers[0].priceObservedAtUtc, "2026-10-25T08:00:00Z");
});

test("la fecha de precio y la de consulta de un proveedor son hechos distintos", () => {
  const [entry] = normalizeCatalogFreshnessPage(page(), "all").items[0].providers;
  assert.equal(entry.priceObservedAtUtc, "2026-10-25T08:00:00Z");
  assert.equal(entry.lastCheckedAtUtc, "2026-10-26T06:30:00Z");

  // Consulta sin respuesta de precio: la consulta se registra, el precio queda desconocido (nunca 0 ni el de
  // otro proveedor).
  const unknown = normalizeCatalogFreshnessPage(
    page({ items: [item({ providers: [provider({ priceObservedAtUtc: null })] })] }),
    "all"
  );
  assert.equal(unknown.items[0].providers[0].priceObservedAtUtc, null);
  assert.equal(unknown.items[0].providers[0].lastCheckedAtUtc, "2026-10-26T06:30:00Z");

  // Un juego sin ningún proveedor listado conserva su fila con la lista vacía: el panel dice «sin datos por
  // proveedor», no inventa una tienda.
  const noProviders = normalizeCatalogFreshnessPage(
    page({ items: [item({ providers: undefined })] }),
    "all"
  );
  assert.equal(noProviders.count, 1);
  assert.deepEqual([...noProviders.items[0].providers], []);
});

test("un contador invalido es desconocido, nunca 0, y el total cubre sus cubetas", () => {
  for (const counts of [undefined, null, "x", 7, { total: "", recent: -1, stale: 1.5, missing: "20" }]) {
    const result = normalizeCatalogFreshnessPage(page({ counts }), "all");
    assert.deepEqual(
      { ...result.counts },
      { total: null, recent: null, stale: null, missing: null },
      JSON.stringify(counts)
    );
  }

  // Si el total del servidor no cubre sus propias cubetas, gana la suma: el panel nunca reporta menos juegos
  // de los que sus contadores ya declaran.
  const covered = normalizeCatalogFreshnessPage(page({ counts: { total: 5, recent: 30, stale: 70, missing: 20 } }), "all");
  assert.deepEqual({ ...covered.counts }, { total: 120, recent: 30, stale: 70, missing: 20 });

  // Una cubeta ilegible queda desconocida, pero no borra las demás ni el total.
  const partial = normalizeCatalogFreshnessPage(page({ counts: { total: 90, recent: 30, stale: 70, missing: null } }), "all");
  assert.deepEqual({ ...partial.counts }, { total: 100, recent: 30, stale: 70, missing: null });
});

test("el total seleccionado nunca es menor que las filas recibidas", () => {
  const two = [item({ gameId: 1 }), item({ gameId: 2 })];
  for (const total of [undefined, null, 0, -5, "x"]) {
    const result = normalizeCatalogFreshnessPage(page({ items: two, total }), "all");
    assert.equal(result.total, 2, String(total));
    assert.equal(result.hasMore, false);
  }

  assert.equal(normalizeCatalogFreshnessPage(page({ items: two, total: 9 }), "all").total, 9);
});

test("la paginacion se acota al contrato y hasMore se deriva del total", () => {
  for (const pageNumber of [0, -1, 1.5, "2", null, undefined]) {
    assert.equal(normalizeCatalogFreshnessPage(page({ page: pageNumber }), "all").page, 1, String(pageNumber));
  }
  assert.equal(normalizeCatalogFreshnessPage(page({ page: 1e9 }), "all").page, CATALOG_FRESHNESS_PAGE_MAX);

  for (const size of [0, -20, 1.5, "20", null]) {
    assert.equal(
      normalizeCatalogFreshnessPage(page({ pageSize: size }), "all").pageSize,
      CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT,
      String(size)
    );
  }
  assert.equal(
    normalizeCatalogFreshnessPage(page({ pageSize: 999 }), "all").pageSize,
    CATALOG_FRESHNESS_PAGE_SIZE_MAX
  );

  assert.equal(normalizeCatalogFreshnessPage(page({ page: 1, pageSize: 20, total: 41 }), "all").hasMore, true);
  assert.equal(normalizeCatalogFreshnessPage(page({ page: 2, pageSize: 20, total: 41 }), "all").hasMore, true);
  assert.equal(normalizeCatalogFreshnessPage(page({ page: 3, pageSize: 20, total: 41 }), "all").hasMore, false);
  assert.equal(normalizeCatalogFreshnessPage(page({ page: 1, pageSize: 20, total: 20 }), "all").hasMore, false);
});

test("las horas de frescura del sobre se conservan solo si son validas", () => {
  assert.equal(normalizeCatalogFreshnessPage(page({ freshnessHours: 72 }), "all").freshnessHours, 72);
  // El número que pinta la UI es el que el servidor usó para clasificar, no una constante de la pantalla.
  assert.equal(normalizeCatalogFreshnessPage(page({ freshnessHours: 48 }), "all").freshnessHours, 48);
  for (const hours of [undefined, null, 0, -72, 72.5, "72"]) {
    assert.equal(
      normalizeCatalogFreshnessPage(page({ freshnessHours: hours }), "all").freshnessHours,
      CATALOG_FRESHNESS_HOURS,
      String(hours)
    );
  }
});

test("el filtro y la pagina viajan por el BFF solo con valores validos", () => {
  assert.deepEqual([...CATALOG_FRESHNESS_FILTERS], ["all", "recent", "stale", "missing"]);
  for (const value of CATALOG_FRESHNESS_FILTERS) {
    assert.equal(parseCatalogFreshnessFilterParam(value), value);
    assert.equal(parseCatalogFreshnessFilterParam(`  ${value.toUpperCase()} `), value);
    assert.equal(isCatalogFreshnessFilter(value), true);
  }
  for (const value of [null, "", "  ", "fresh", "old", "recent,stale", "0"]) {
    assert.equal(parseCatalogFreshnessFilterParam(value), null, String(value));
    assert.equal(isCatalogFreshnessFilter(value), false, String(value));
  }

  for (const value of ["1", "3", "50", String(CATALOG_FRESHNESS_PAGE_MAX)]) {
    assert.equal(parseCatalogFreshnessPageParam(value), Number(value));
  }
  for (const value of [null, "", "0", "-1", "1.5", "01", " 1 ", "1e3", "1".repeat(12)]) {
    assert.equal(parseCatalogFreshnessPageParam(value), null, String(value));
  }
});

test("el filtro solicitado es la unica fuente de la etiqueta de la pagina", () => {
  const payload = page({ status: "stale", filter: "stale" });
  assert.equal(normalizeCatalogFreshnessPage(payload, "missing").filter, "missing");
  assert.equal(normalizeCatalogFreshnessPage(payload, "recent").filter, "recent");
  assert.equal(normalizeCatalogFreshnessPage(payload, "all").filter, "all");
});

test("un juego repetido en la pagina se pinta una sola vez y conserva el orden", () => {
  const result = normalizeCatalogFreshnessPage(
    page({
      items: [item({ gameId: 2, name: "B" }), item({ gameId: 5, name: "A" }), item({ gameId: 2, name: "B otra vez" })]
    }),
    "all"
  );
  assert.deepEqual(
    result.items.map((row) => row.gameId),
    [2, 5]
  );
  assert.equal(result.items[0].name, "B");
  assert.equal(result.count, 2);
});

test("una pagina jamas materializa mas filas que el tope del contrato", () => {
  const items = Array.from({ length: MAX_CATALOG_FRESHNESS_ITEMS + 30 }, (_, index) => item({ gameId: index + 1 }));
  const result = normalizeCatalogFreshnessPage(page({ items, total: 500 }), "all");
  assert.equal(result.items.length, MAX_CATALOG_FRESHNESS_ITEMS);
  assert.equal(result.count, MAX_CATALOG_FRESHNESS_ITEMS);
  assert.equal(result.total, 500);
});

test("un appid o una portada invalidos no llegan al render", () => {
  for (const steamAppId of [null, 0, -1, 1.5, "1057800", true]) {
    const result = normalizeCatalogFreshnessPage(page({ items: [item({ steamAppId })] }), "all");
    assert.equal(result.items[0].steamAppId, null, String(steamAppId));
  }
  for (const imageUrl of [null, "", "/cover.jpg", "http://cdn.example/x.jpg", "javascript:alert(1)", 42]) {
    const result = normalizeCatalogFreshnessPage(page({ items: [item({ imageUrl })] }), "all");
    assert.equal(result.items[0].imageUrl, null, String(imageUrl));
  }

  const valid = normalizeCatalogFreshnessPage(
    page({ items: [item({ steamAppId: 620, imageUrl: "https://cdn.example/x.jpg" })] }),
    "all"
  );
  assert.equal(valid.items[0].steamAppId, 620);
  assert.equal(valid.items[0].imageUrl, "https://cdn.example/x.jpg");
});

test("los contadores salen del mismo sobre, sin pedidos independientes", () => {
  const result = normalizeCatalogFreshnessPage(page(), "stale");
  assert.equal(catalogFreshnessCount(result, "all"), 120);
  assert.equal(catalogFreshnessCount(result, "recent"), 30);
  assert.equal(catalogFreshnessCount(result, "stale"), 70);
  assert.equal(catalogFreshnessCount(result, "missing"), 20);

  assert.equal(catalogFreshnessCount(null, "all"), null);
  assert.equal(catalogFreshnessCount(normalizeCatalogFreshnessPage({ counts: { total: "x" } }, "all"), "recent"), null);

  // La misma lectura sobre el bloque de contadores, que es lo que el panel conserva cuando una página falla.
  assert.equal(freshnessCountFromCounts(null, "stale"), null);
  assert.equal(freshnessCountFromCounts(result.counts, "missing"), 20);
});

// --- Estructura del BFF y del panel (misma práctica que `tests/jobs-routes.test.mjs`: lectura estática, sin
// red ni base de datos). Fijan que la única superficie nueva sea de LECTURA y que el panel se monte en
// `/discover` sin tocar las tres pestañas de ofertas.

test("el BFF de frescura solo lee, valida el filtro y conserva los errores tipados", async () => {
  const source = await readFile(new URL("../../app/api/bff/discover/freshness/route.ts", import.meta.url), "utf8");

  assert.match(source, /export async function GET/);
  assert.doesNotMatch(source, /export async function (POST|PUT|PATCH|DELETE)/);
  assert.match(source, /getServerSession/);
  assert.match(source, /unauthorized\(request\)/);
  assert.match(source, /badRequest\(/);
  assert.match(source, /parseCatalogFreshnessFilterParam/);
  assert.match(source, /parseCatalogFreshnessPageParam/);
  assert.match(source, /\/api\/catalogdeals\/freshness/);
  assert.match(source, /fetchApiWithAutoRefresh/);
  assert.match(source, /attachSessionCookie/);
  assert.match(source, /upstreamError\(/);
  assert.match(source, /normalizeCatalogFreshnessPage\(/);
});

test("el panel de frescura se monta en /discover, enlaza a /jobs y no escribe", async () => {
  const panel = await readFile(new URL("../../app/discover/_components/freshness-panel.tsx", import.meta.url), "utf8");
  const api = await readFile(new URL("../../app/discover/_lib/freshness-api.ts", import.meta.url), "utf8");
  const client = await readFile(new URL("../../app/discover/discover-client.tsx", import.meta.url), "utf8");

  assert.match(panel, /href="\/jobs"/);
  // El lanzamiento manual vive en /jobs: desde /discover no hay ninguna escritura.
  assert.doesNotMatch(panel, /csrfFetch/);
  assert.doesNotMatch(api, /csrfFetch|method: "POST"/);
  assert.match(api, /\/api\/bff\/discover\/freshness/);
  assert.match(api, /parseApiError/);
  assert.match(api, /normalizeCatalogFreshnessPage/);

  // El panel se agrega; las tres categorías de ofertas y su copy siguen en la página.
  assert.match(client, /<CatalogFreshnessPanel \/>/);
  assert.match(client, /DISCOVER_CATEGORIES\.map/);
  assert.match(client, /CATEGORY_COPY/);
});

// --- Respuesta upstream ilegible (revisión independiente, aviso aceptado). Un 2xx cuyo cuerpo no es JSON
// (HTML de un proxy, cuerpo truncado, stream cortado) NO puede convertirse en un 500 sin tipo que además
// descarte la cookie de sesión recién rotada: tiene que ser un error tipado del BFF con su traceId.

test("un cuerpo upstream ilegible es un fallo de lectura, nunca una excepción sin tipo", async () => {
  // Cuerpo válido: se conserva tal cual (el normalizador decide después si sirve).
  const valid = await readUpstreamJson({ json: async () => ({ total: 3 }) });
  assert.equal(valid.ok, true);
  assert.deepEqual(valid.ok ? valid.body : null, { total: 3 });

  // JSON legible pero vacío sigue siendo un cuerpo leído: degrada a página vacía, no a error de transporte.
  const empty = await readUpstreamJson({ json: async () => null });
  assert.equal(empty.ok, true);
  assert.equal(empty.ok ? empty.body : "x", null);

  // `json()` que rechaza no escapa: se convierte en un resultado sin cuerpo.
  for (const json of [
    async () => {
      throw new SyntaxError("Unexpected token '<', \"<!doctype html>\"... is not valid JSON");
    },
    async () => {
      throw new TypeError("terminated");
    },
    () => {
      throw new Error("sync throw");
    }
  ]) {
    const unreadable = await readUpstreamJson({ json });
    assert.equal(unreadable.ok, false);
    assert.equal("body" in unreadable, false);
  }
});

test("la ruta nueva convierte el 2xx ilegible en error tipado y conserva la cookie rotada", async () => {
  const source = await readFile(new URL("../../app/api/bff/discover/freshness/route.ts", import.meta.url), "utf8");

  // La lectura del cuerpo pasa por el helper guardado, no por un `await response.json()` suelto en el 2xx.
  assert.match(source, /import \{ readUpstreamJson \} from "\.\/read-upstream-body"/);
  assert.match(source, /const body = await readUpstreamJson\(response\);/);
  assert.doesNotMatch(source, /normalizeCatalogFreshnessPage\(await response\.json\(\)/);

  // La rama del cuerpo ilegible devuelve un error upstream tipado (502 -> UPSTREAM_ERROR) y adjunta la sesión
  // rotada exactamente con los helpers existentes.
  const branch = source.match(/if \(!body\.ok\) \{([\s\S]*?)return result;/);
  assert.ok(branch, "el camino exitoso del BFF guarda la lectura del cuerpo");
  assert.match(branch[1], /upstreamError\(\s*request,\s*502,/);
  assert.match(branch[1], /attachSessionCookie\(result, updatedSession, session\)/);
  assert.match(branch[1], /No se pudo leer el estado de actualización de precios/);

  // El cuerpo legible sigue normalizándose con el filtro pedido y adjunta la cookie rotada.
  assert.match(source, /normalizeCatalogFreshnessPage\(body\.body, filter\)/);
  assert.match(source, /attachSessionCookie\(result, updatedSession, session\);/);
});

// --- Corrección S4: el panel y su BFF son de la LISTA DE DESEOS del solicitante y el título es exacto.
// El usuario corrigió el alcance: «Frescura del catálogo conocido esto no es lo que quiero es solo whislist
// y frescura n se usa como palabra», y confirmó «Sí, panel y job solo de mi wishlist». Estas pruebas fijan
// que la superficie nueva no vuelva a prometer el catálogo completo ni use «frescura» como palabra visible.

const MONITORING_HEADING = "Estado de actualización de precios";

test("el panel usa el título exacto, habla solo de la lista de deseos y no dice «frescura»", async () => {
  const panel = await readFile(new URL("../../app/discover/_components/freshness-panel.tsx", import.meta.url), "utf8");

  // Título exacto acordado con el usuario.
  assert.match(panel, new RegExp(`>\\s*${MONITORING_HEADING}\\s*<`));
  // «frescura» no se usa como palabra en la superficie nueva (ni en el copy ni en sus comentarios).
  assert.doesNotMatch(panel, /frescura/i);
  // El alcance es la lista de deseos del usuario autenticado, no el catálogo completo ni la biblioteca.
  assert.match(panel, /lista de deseos/i);
  assert.doesNotMatch(panel, /catálogo completo|catálogo conocido|Todo el catálogo|juegos canónicos/i);
  // El filtro «todo» también es de la lista, no del catálogo.
  assert.match(panel, /Toda mi lista de deseos/);
});

test("el enlace al job explica el lote pendiente de la lista de deseos y lo no soportado", async () => {
  const panel = await readFile(new URL("../../app/discover/_components/freshness-panel.tsx", import.meta.url), "utf8");

  assert.match(panel, /catalog\.prices-refresh/);
  // Lote manual y acotado (20) sobre pendientes de la lista, no del catálogo.
  assert.match(panel, /lote de hasta 20 juegos por ejecución/);
  // Honestidad: un juego sin identidad soportada se reporta como no soportado, no se inventa un precio.
  assert.match(panel, /sin ficha en Steam ni identidad de tienda soportada/i);
  assert.match(panel, /no soportado/i);
  // La sección solo muestra lo guardado: el job se lanza en /jobs.
  assert.match(panel, /href="\/jobs"/);
  assert.doesNotMatch(panel, /csrfFetch|method: "POST"/);
});

test("el BFF, el cliente y el subtítulo no prometen el catálogo completo ni usan «frescura»", async () => {
  const route = await readFile(new URL("../../app/api/bff/discover/freshness/route.ts", import.meta.url), "utf8");
  const api = await readFile(new URL("../../app/discover/_lib/freshness-api.ts", import.meta.url), "utf8");
  const page = await readFile(new URL("../../app/discover/page.tsx", import.meta.url), "utf8");

  for (const source of [route, api]) {
    assert.doesNotMatch(source, /frescura/i);
    assert.match(source, /lista de deseos/i);
  }
  // El subtítulo es el original de las tres pestañas de ofertas: la sección nueva no lo reescribe.
  assert.match(page, /Menos de \$50 MXN, mínimos verificados y mejores ofertas del catálogo conocido\./);
  assert.doesNotMatch(page, /frescura/i);
});

// --- Entradas de la lista de deseos sin identidad reconocida (corrección S4, verificación independiente).
// El backend las reporta en un contador APARTE, aditivo y opcional: no son juegos canónicos, no tienen id que
// inventar y no entran en las cubetas. Un contador ausente o ilegible es DESCONOCIDO (`null`), nunca 0; y un
// catálogo canónico vacío con entradas sin identificar > 0 tiene que seguir avisando.

test("las entradas sin identificar son un contador aparte: desconocido no es 0", () => {
  const known = normalizeCatalogFreshnessPage(page({ unresolvedWishlistEntries: 7 }), "all");
  assert.equal(known.unresolvedWishlistEntries, 7);
  // No se mezclan con las cubetas canónicas ni con el total del filtro seleccionado.
  assert.deepEqual({ ...known.counts }, { total: 120, recent: 30, stale: 70, missing: 20 });
  assert.equal(known.total, 41);
  assert.equal(known.count, 1);

  for (const raw of [undefined, null, -1, 1.5, "3", true, {}, []]) {
    const result = normalizeCatalogFreshnessPage(page({ unresolvedWishlistEntries: raw }), "all");
    assert.equal(result.unresolvedWishlistEntries, null, JSON.stringify(raw));
  }

  // Un 0 conocido es un dato, no un desconocido.
  assert.equal(normalizeCatalogFreshnessPage(page({ unresolvedWishlistEntries: 0 }), "all").unresolvedWishlistEntries, 0);
  // Sobre vacío: desconocido, nunca 0.
  assert.equal(normalizeCatalogFreshnessPage({}, "all").unresolvedWishlistEntries, null);
});

test("con el catálogo canónico en 0, un contador sin identificar > 0 sigue avisando", () => {
  const allUnresolved = normalizeCatalogFreshnessPage(
    page({
      total: 0,
      counts: { total: 0, recent: 0, stale: 0, missing: 0 },
      items: [],
      unresolvedWishlistEntries: 4
    }),
    "all"
  );

  assert.equal(allUnresolved.total, 0);
  assert.equal(allUnresolved.count, 0);
  assert.deepEqual([...allUnresolved.items], []);
  assert.equal(unresolvedWishlistEntriesCount(allUnresolved), 4);
  assert.equal(hasUnresolvedWishlistEntries(allUnresolved), true);

  // Un 0 conocido, un desconocido o la ausencia de página no inventan aviso.
  assert.equal(hasUnresolvedWishlistEntries(normalizeCatalogFreshnessPage(page({ unresolvedWishlistEntries: 0 }), "all")), false);
  assert.equal(hasUnresolvedWishlistEntries(normalizeCatalogFreshnessPage({}, "all")), false);
  assert.equal(hasUnresolvedWishlistEntries(null), false);
  assert.equal(unresolvedWishlistEntriesCount(null), null);
});

test("el panel avisa las entradas sin identificar junto a los contadores, sin contarlas como juego", async () => {
  const panel = await readFile(new URL("../../app/discover/_components/freshness-panel.tsx", import.meta.url), "utf8");

  // Aviso visible junto a los contadores, con el número del contador aparte.
  assert.match(panel, /Entradas de tu lista de deseos sin identificar/);
  assert.match(panel, /no se cuentan en las cubetas/i);
  assert.match(panel, /refresco manual no puede actualizarlas/i);
  assert.match(panel, /identidad se resuelva/i);
  // El aviso se decide por el contador aparte (puede haber 0 juegos canónicos y N entradas sin identificar).
  assert.match(panel, /unresolvedWishlistEntries/);
  assert.match(panel, /hasUnresolvedWishlistEntries/);
  // El título exacto sigue igual y no vuelve la palabra retirada.
  assert.match(panel, /Estado de actualización de precios/);
  assert.doesNotMatch(panel, /frescura/i);
});

// --- Corrección S9: el panel clasifica por el precio más barato GUARDADO, no por el precio más reciente entre
// todas las tiendas. El usuario eligió esa definición tras ver 40 juegos bajo MX$50 en su lista de deseos
// frente a 7 en `/discover`, y 512 de 604 juegos marcados como recientes. Estas pruebas fijan que la fila
// trae el precio ganador, su tienda, su fuente y su tipo de la MISMA fila, y que la fecha del precio es la de
// ese ganador: un Steam reciente y caro no puede tapar un precio externo antiguo y barato (ni al revés).

test("el precio elegido, su tienda y su fecha salen de la misma fila ganadora", () => {
  const row = normalizeCatalogFreshnessPage(
    page({
      items: [
        item({
          // Ganador: 30.00 MXN de gg.deals observado hace 10 días. Steam respondió hace 2 h por 399.00 MXN,
          // pero ese precio no es el más barato guardado y no puede ser el que la fila muestre como propio.
          status: "stale",
          priceObservedAtUtc: "2026-10-16T10:00:00Z",
          chosenPriceMinor: 3000,
          chosenShopName: "GG.deals",
          chosenSource: "ggdeals",
          chosenPricingType: "regional",
          providers: [
            provider({ provider: "ggdeals", priceObservedAtUtc: "2026-10-16T10:00:00Z" }),
            provider({ provider: "steam", priceObservedAtUtc: "2026-10-26T10:00:00Z" })
          ]
        })
      ]
    }),
    "all"
  ).items[0];

  assert.equal(row.status, "stale");
  // La fecha del precio es la del ganador (10 días), no la del Steam reciente de 2 h.
  assert.equal(row.priceObservedAtUtc, "2026-10-16T10:00:00Z");
  assert.deepEqual({ ...row.chosenOffer }, {
    priceMinor: 3000,
    shopName: "GG.deals",
    source: "ggdeals",
    pricingType: "regional",
    usesFxEstimate: false
  });
  assert.equal(chosenOfferStoreLabel(row.chosenOffer), "GG.deals");
  assert.equal(formatChosenOfferPrice(row.chosenOffer), "$30.00");
});

test("un monto ilegible es desconocido, nunca 0, y sin monto no se pinta precio", () => {
  for (const raw of [
    undefined,
    null,
    "",
    "3000",
    -1,
    1.5,
    Number.NaN,
    Number.POSITIVE_INFINITY,
    MAX_CHOSEN_PRICE_MINOR + 1,
    true,
    {},
    []
  ]) {
    const row = normalizeCatalogFreshnessPage(page({ items: [item({ chosenPriceMinor: raw })] }), "all").items[0];
    assert.equal(row.chosenOffer, null, String(raw));
    // Un precio desconocido no se convierte en «$0.00» ni se pinta sin su monto.
    assert.equal(formatChosenOfferPrice(row.chosenOffer), null, String(raw));
    assert.equal(chosenOfferStoreLabel(row.chosenOffer), null, String(raw));
  }

  // 0 es un monto VÁLIDO (un juego gratis): lo desconocido es la ausencia de monto, no el cero.
  const free = normalizeCatalogFreshnessPage(
    page({ items: [item({ chosenPriceMinor: 0, chosenPricingType: "regional" })] }),
    "all"
  ).items[0];
  assert.equal(free.chosenOffer?.priceMinor, 0);
  assert.equal(free.chosenOffer?.usesFxEstimate, false);
  assert.equal(formatChosenOfferPrice(free.chosenOffer), "$0.00");
});

test("el «≈» aparece solo con fx_estimate y la tienda sale del mismo ganador, nunca de otra fila", () => {
  const estimate = normalizeCatalogFreshnessPage(
    page({
      items: [
        item({
          chosenPriceMinor: 16199,
          chosenShopName: "Epic Games Store",
          chosenSource: "epic",
          chosenPricingType: "fx_estimate"
        })
      ]
    }),
    "all"
  ).items[0];
  assert.equal(estimate.chosenOffer?.usesFxEstimate, true);
  assert.equal(formatChosenOfferPrice(estimate.chosenOffer), "≈ $161.99");
  assert.equal(chosenOfferStoreLabel(estimate.chosenOffer), "Epic Games Store");

  // Un tipo ilegible o ausente no es «regional»: se pinta el monto sin «≈» y sin inventar una conversión.
  for (const pricingType of [undefined, null, "", "regional", "unconverted", "estimate", "FX_ESTIMATE", 42]) {
    const row = normalizeCatalogFreshnessPage(
      page({ items: [item({ chosenPricingType: pricingType, chosenShopName: "Steam" })] }),
      "all"
    ).items[0];
    assert.equal(row.chosenOffer?.usesFxEstimate, false, String(pricingType));
    assert.equal(
      formatChosenOfferPrice(row.chosenOffer),
      "$30.00",
      String(pricingType)
    );
    assert.equal(
      row.chosenOffer?.pricingType,
      pricingType === "regional" ? "regional" : null,
      String(pricingType)
    );
  }

  // Sin nombre de tienda se usa el código de la fuente DEL MISMO ganador…
  const sourceOnly = normalizeCatalogFreshnessPage(
    page({
      items: [
        item({
          chosenShopName: null,
          chosenSource: "itad",
          providers: [provider({ provider: "steam", priceObservedAtUtc: "2026-10-26T10:00:00Z" })]
        })
      ]
    }),
    "all"
  ).items[0];
  assert.equal(chosenOfferStoreLabel(sourceOnly.chosenOffer), "IsThereAnyDeal");

  // …y si tampoco hay fuente, no se toma la tienda de otra fila: el precio queda sin etiqueta, nunca «Steam».
  const anonymous = normalizeCatalogFreshnessPage(
    page({
      items: [
        item({
          chosenShopName: null,
          chosenSource: null,
          providers: [provider({ provider: "steam", priceObservedAtUtc: "2026-10-26T10:00:00Z" })]
        })
      ]
    }),
    "all"
  ).items[0];
  assert.equal(anonymous.chosenOffer?.priceMinor, 3000);
  assert.equal(chosenOfferStoreLabel(anonymous.chosenOffer), null);

  // Un nombre con espacios se recorta; uno ilegible es desconocido, no una tienda inventada.
  const spaced = normalizeCatalogFreshnessPage(
    page({ items: [item({ chosenShopName: "  GG.deals  " })] }),
    "all"
  ).items[0];
  assert.equal(chosenOfferStoreLabel(spaced.chosenOffer), "GG.deals");
  const blank = normalizeCatalogFreshnessPage(
    page({ items: [item({ chosenShopName: "   ", chosenSource: "" })] }),
    "all"
  ).items[0];
  assert.equal(chosenOfferStoreLabel(blank.chosenOffer), null);

  // Un nombre desmedido se acota en vez de reventar el ancho de una columna de seis.
  const longName = normalizeCatalogFreshnessPage(
    page({ items: [item({ chosenShopName: "T".repeat(400) })] }),
    "all"
  ).items[0];
  assert.equal(chosenOfferStoreLabel(longName.chosenOffer)?.length, MAX_CHOSEN_SHOP_NAME);
});

test("un juego sin precio comparable no puede exhibir un monto elegido", () => {
  const row = normalizeCatalogFreshnessPage(
    page({
      items: [
        item({
          status: "missing",
          priceObservedAtUtc: "2026-10-01T00:00:00Z",
          chosenPriceMinor: 3000,
          chosenShopName: "GG.deals",
          providers: [provider({ priceObservedAtUtc: null })]
        })
      ]
    }),
    "all"
  ).items[0];

  assert.equal(row.status, "missing");
  // «No tiene precio» y «su precio más barato es 30.00» no pueden ser ciertas a la vez.
  assert.equal(row.priceObservedAtUtc, null);
  assert.equal(row.chosenOffer, null);
  assert.equal(formatChosenOfferPrice(row.chosenOffer), null);
});

test("las etiquetas cortas del estado son ≤72 h, +72 h y Sin precio", () => {
  assert.equal(freshnessShortStatusLabel("recent", 72), "≤72 h");
  assert.equal(freshnessShortStatusLabel("stale", 72), "+72 h");
  assert.equal(freshnessShortStatusLabel("missing", 72), "Sin precio");
  // El número es el que el servidor usó para clasificar, no una constante de la pantalla.
  assert.equal(freshnessShortStatusLabel("recent", 48), "≤48 h");
  assert.equal(freshnessShortStatusLabel("stale", 48), "+48 h");
});

// --- Layout y accesibilidad del panel (lectura estática, misma práctica que el resto del archivo).

test("las tarjetas del panel son compactas y responden a 1, 2, 3 y 6 columnas", async () => {
  const panel = await readFile(new URL("../../app/discover/_components/freshness-panel.tsx", import.meta.url), "utf8");

  // Una columna en móvil y hasta seis en escritorio ancho.
  assert.match(panel, /<ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-6">/);
  // Título corto y portada pequeña en cada tarjeta.
  assert.match(panel, /line-clamp-1/);
  assert.match(panel, /aspect-video/);
  // Las etiquetas cortas vienen del contrato (misma fuente que las pestañas de filtro), no de un literal por estado.
  assert.match(panel, /freshnessShortStatusLabel\(item\.status, hours\)/);
  assert.doesNotMatch(panel, /Más antiguo que/);
  assert.doesNotMatch(panel, /Sin precio comparable"/);
});

test("el precio de la tarjeta lleva su tienda y su fecha, las tres del mismo ganador", async () => {
  const panel = await readFile(new URL("../../app/discover/_components/freshness-panel.tsx", import.meta.url), "utf8");

  assert.match(panel, /formatChosenOfferPrice\(item\.chosenOffer\)/);
  assert.match(panel, /chosenOfferStoreLabel\(item\.chosenOffer\)/);
  // La fecha que acompaña al precio es la de la fila ganadora.
  assert.match(panel, /formatMoment\(item\.priceObservedAtUtc\)/);
  // El componente no lee un monto por su cuenta (ni de `providers`, ni de un segundo campo de precio).
  assert.equal(panel.match(/priceMinor/g), null);
});

test("las fechas por proveedor quedan en un colapsable nativo y accesible, no en una lista siempre visible", async () => {
  const panel = await readFile(new URL("../../app/discover/_components/freshness-panel.tsx", import.meta.url), "utf8");

  // `<details>`/`<summary>` nativos: se abren con teclado sin JavaScript propio y conservan su anuncio.
  const details = panel.match(/<details[\s\S]*?<\/details>/);
  assert.ok(details, "el detalle por proveedor vive dentro de un <details>");
  assert.match(details[0], /providers\.map/);
  assert.match(details[0], /<summary className="[^"]*focus-visible:ring-2/);
  assert.match(details[0], /Fechas por proveedor/);
  // Una sola lista de fechas por tarjeta: ninguna copia quedó visible fuera del colapsable.
  assert.equal(panel.match(/providers\.map/g)?.length, 1);
});

test("la introducción dice el estado y la fecha del precio más barato guardado y enlaza la pestaña de MX$50", async () => {
  const panel = await readFile(new URL("../../app/discover/_components/freshness-panel.tsx", import.meta.url), "utf8");

  // Frase pedida por el usuario: qué muestra la sección y qué NO afirma.
  assert.match(panel, /Estado y fecha del precio más barato guardado; no implica oferta vigente\./);
  // Explica la diferencia con la pestaña existente sin atribuirle una causa al 40/7 reportado.
  assert.match(panel, /Menos de MX\$50/);
  assert.match(panel, /no cuentan lo mismo/i);
  assert.match(panel, /no implica|ninguna de las dos es un error/i);
  assert.doesNotMatch(panel, /\b40\b|\b7\b/);
  // La palabra retirada sigue fuera de la superficie nueva.
  assert.doesNotMatch(panel, /frescura/i);
});
