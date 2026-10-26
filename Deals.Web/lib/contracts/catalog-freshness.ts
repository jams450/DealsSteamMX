// Contrato del panel «Estado de actualización de precios» de la LISTA DE DESEOS del usuario
// (`GET /api/bff/discover/freshness`, spec S4/S7/S9, tarea T3).
//
// El sobre es el del backend (`GET /api/catalogdeals/freshness?status&page&pageSize`): una página de UN filtro
// más los CUATRO contadores de ESA lista de deseos, así que cambiar de filtro nunca dispara cuatro pedidos.
//
// Qué significan los números, y qué NO significan (S2/S4/S7/S9):
//   · `recent` = la fecha del precio comparable MÁS BARATO guardado está dentro de la ventana; `stale` = ese
//     precio ganador es más antiguo; `missing` = no hay ningún precio comparable. Son cubetas de COBERTURA DE
//     PRECIOS, no la prueba de que todas las tiendas se refrescaron: un juego `recent` puede tener un
//     proveedor sin consultar, y para eso están las fechas por proveedor.
//   · «antiguo» no es «erróneo»: una fecha vieja solo dice que no se ha vuelto a observar.
//   · «sin precio comparable» no prueba que la consulta falló: puede ser una tienda que no vende el juego, una
//     identidad que aún no se resuelve, o un proveedor no soportado.
//   · la fecha de precio por proveedor (`priceObservedAtUtc`) y la de su última consulta (`lastCheckedAtUtc`)
//     son hechos distintos; la segunda sin la primera es una consulta sin precio, no un precio.
//   · una fecha futura no es evidencia de actualización válida, y un campo ilegible es DESCONOCIDO (`null`), nunca 0.
//
// Corrección S9: las cubetas se deciden por el precio comparable MÁS BARATO guardado, no por el precio más
// reciente entre todas las tiendas. Un Steam caro y reciente no puede tapar un precio externo antiguo y barato.
// Por eso la fila trae ADITIVAMENTE `chosenPriceMinor`, `chosenShopName`, `chosenSource` y `chosenPricingType`
// de esa misma oferta ganadora, y `priceObservedAtUtc` es la fecha de ESE ganador. Precio, tienda, tipo y fecha
// son una sola fila: el panel nunca los combina entre proveedores distintos.

import { formatCurrency } from "../format/currency.ts";

export const CATALOG_FRESHNESS_FILTERS = ["all", "recent", "stale", "missing"] as const;
export type CatalogFreshnessFilter = (typeof CATALOG_FRESHNESS_FILTERS)[number];

// Estado de UNA fila: nunca `all`.
export const CATALOG_FRESHNESS_STATUSES = ["recent", "stale", "missing"] as const;
export type CatalogFreshnessStatus = (typeof CATALOG_FRESHNESS_STATUSES)[number];

// Topes del contrato. Acotan el render y el offset de una query manipulada; el backend acota los mismos
// valores, así que el panel no puede pedir una página que la API vaya a rechazar.
export const CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT = 20;
export const CATALOG_FRESHNESS_PAGE_SIZE_MAX = 50;
export const CATALOG_FRESHNESS_PAGE_MAX = 1_000_000;
export const MAX_CATALOG_FRESHNESS_ITEMS = CATALOG_FRESHNESS_PAGE_SIZE_MAX;

// Ventana de actualización del contrato (S6: 72 h, nunca la TTL de proveedor de 7 días). Es solo el valor por
// defecto: el número que se pinta es el que el servidor informó en `freshnessHours`, porque es el que usó
// para clasificar las filas.
export const CATALOG_FRESHNESS_HOURS = 72;

// Tipos de precio del ganador. `fx_estimate` es una CONVERSIÓN, así que su monto se pinta con «≈»;
// `regional` es el precio de la tienda en MXN y se pinta exacto. Un tipo ausente o ilegible queda DESCONOCIDO
// (`null`): se pinta el monto sin «≈» y sin afirmar que es regional (mismo criterio que `lib/contracts/discover.ts`).
export const CATALOG_FRESHNESS_PRICING_TYPES = ["regional", "fx_estimate"] as const;
export type CatalogFreshnessPricingType = (typeof CATALOG_FRESHNESS_PRICING_TYPES)[number];

// Topes de los campos del ganador: un nombre de tienda desmedido no infla la tarjeta y un monto absurdo no se
// pinta. 1e11 centavos son mil millones de pesos: por encima de eso el dato no es un precio creíble.
export const MAX_CHOSEN_SHOP_NAME = 128;
export const MAX_CHOSEN_PRICE_MINOR = 100_000_000_000;

// Cada juego puede listar varios proveedores; el tope existe para que un payload absurdo no infle el DOM.
export const MAX_FRESHNESS_PROVIDERS = 16;

// Tolerancia de desfase de relojes entre la base y el proceso que arma `asOfUtc`: una fecha de precio que
// excede `asOfUtc` por más que esto no es evidencia de actualización válida.
export const FRESHNESS_FUTURE_TOLERANCE_MS = 5 * 60 * 1000;

// Etiquetas legibles de los códigos de proveedor/fuente que llegan del snapshot. Un código desconocido se pinta
// tal cual: no se inventa una tienda ni se toma el nombre de otra fila.
const SOURCE_LABELS: Record<string, string> = {
  steam: "Steam",
  itad: "IsThereAnyDeal",
  ggdeals: "GG.deals",
  epic: "Epic Games Store",
  microsoft: "Microsoft Store"
};

export function freshnessSourceLabel(source: string): string {
  return SOURCE_LABELS[source.toLowerCase()] ?? source;
}

// La oferta ganadora: el precio comparable más barato guardado del juego, con su tienda, su fuente y su tipo.
// Los cuatro campos vienen de la MISMA fila persistida, así que el panel no puede pintar el nombre de una tienda
// sobre el precio o la fecha de otra. `priceMinor` está en unidades menores (centavos) y puede ser 0: un juego
// gratis tiene precio; lo que no existe es un monto desconocido.
export type CatalogFreshnessChosenOffer = {
  readonly priceMinor: number;
  readonly shopName: string | null;
  readonly source: string | null;
  readonly pricingType: CatalogFreshnessPricingType | null;
  // Derivado de `pricingType`, nunca de un booleano del payload: SOLO `fx_estimate` es una conversión.
  readonly usesFxEstimate: boolean;
};

export type CatalogFreshnessProvider = {
  readonly provider: string;
  // Última observación de precio de ESE proveedor (null: no hay precio observado, no hay cero).
  readonly priceObservedAtUtc: string | null;
  // Última consulta a ESE proveedor (null: sin registro; no prueba que nunca se intentó).
  readonly lastCheckedAtUtc: string | null;
};

export type CatalogFreshnessItem = {
  readonly gameId: number;
  readonly name: string;
  readonly imageUrl: string | null;
  // Nullable y NUNCA derivado de `gameId`: un juego que Steam no vende no tiene ficha interna.
  readonly steamAppId: number | null;
  readonly status: CatalogFreshnessStatus;
  // Fecha de observación del precio GANADOR (el más barato guardado), no la más reciente entre proveedores.
  readonly priceObservedAtUtc: string | null;
  // `null` cuando no hay monto legible (o cuando el estado es `missing`, que niega tener precio comparable).
  readonly chosenOffer: CatalogFreshnessChosenOffer | null;
  readonly providers: readonly CatalogFreshnessProvider[];
};

export type CatalogFreshnessCounts = {
  // Desconocido (`null`) se pinta «—»: un contador ilegible no se convierte en «no hay juegos».
  readonly total: number | null;
  readonly recent: number | null;
  readonly stale: number | null;
  readonly missing: number | null;
};

export type CatalogFreshnessPage = {
  // Filtro PEDIDO: única fuente de la etiqueta de la página (una respuesta atrasada no la renombra).
  readonly filter: CatalogFreshnessFilter;
  readonly asOfUtc: string | null;
  readonly freshnessHours: number;
  readonly page: number;
  readonly pageSize: number;
  // Total del filtro seleccionado (nunca menor que las filas recibidas).
  readonly total: number;
  readonly counts: CatalogFreshnessCounts;
  // Entradas de la lista de deseos sin juego/tienda reconocidos. Es ADITIVO y opcional en el sobre: no son
  // juegos canónicos (no se inventa un `gameId`), no entran en `counts`/`items` y el refresco manual no puede
  // actualizarlas. Desconocido (`null`) se lee «—», nunca 0.
  readonly unresolvedWishlistEntries: number | null;
  readonly items: readonly CatalogFreshnessItem[];
  // Cuántas filas pinta ESTA página después de descartar payload inválido, no lo que el sobre afirmó.
  readonly count: number;
  // Derivado de `page * pageSize < total`: el sobre no envía `hasMore`.
  readonly hasMore: boolean;
};

const MAX_NAME_TEXT = 512;
const MAX_PROVIDER_TEXT = 32;

const FILTER_SET = new Set<string>(CATALOG_FRESHNESS_FILTERS);
const STATUS_SET = new Set<string>(CATALOG_FRESHNESS_STATUSES);
const PRICING_TYPE_SET = new Set<string>(CATALOG_FRESHNESS_PRICING_TYPES);

export function isCatalogFreshnessFilter(value: unknown): value is CatalogFreshnessFilter {
  return typeof value === "string" && FILTER_SET.has(value);
}

// Etiqueta corta del estado de un juego. El número de horas es el que el servidor informó, así que una ventana
// distinta de 72 se lee con su propio número («≤48 h»). Es la misma etiqueta de las pestañas de filtro: el
// distintivo de una tarjeta y el botón que la selecciona dicen exactamente lo mismo.
export function freshnessShortStatusLabel(status: CatalogFreshnessStatus, hours: number): string {
  switch (status) {
    case "recent":
      return `≤${hours} h`;
    case "stale":
      return `+${hours} h`;
    default:
      return "Sin precio";
  }
}

// Tienda del ganador: su nombre guardado y, si no lo hay, la etiqueta de su propia fuente. Sin ninguno de los
// dos el precio se queda sin etiqueta — nunca se toma el nombre de Steam ni el de otro proveedor de la fila.
export function chosenOfferStoreLabel(offer: CatalogFreshnessChosenOffer | null): string | null {
  if (offer === null) return null;
  if (offer.shopName !== null) return offer.shopName;
  return offer.source === null ? null : freshnessSourceLabel(offer.source);
}

// Monto del ganador en MXN con «≈» SOLO cuando el precio es una conversión (`fx_estimate`). Sin monto legible
// devuelve `null`: el panel escribe que no hay monto guardado en vez de pintar «$0.00».
export function formatChosenOfferPrice(offer: CatalogFreshnessChosenOffer | null): string | null {
  if (offer === null) return null;
  const amount = formatCurrency(offer.priceMinor / 100, "es-MX", "MXN");
  return offer.usesFxEstimate ? `≈ ${amount}` : amount;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

// Tolera camelCase (ASP.NET Core por defecto) y PascalCase (mismo patrón que el contrato de `/discover`).
function read(value: Record<string, unknown>, key: string): unknown {
  return value[key] ?? value[key.charAt(0).toUpperCase() + key.slice(1)];
}

function toText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function toBoundedText(value: unknown, maxLength: number): string | null {
  const text = toText(value);
  return text === null ? null : text.slice(0, maxLength);
}

function toNonNegativeInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : null;
}

function toPositiveInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : null;
}

// Monto en unidades menores: entero, no negativo y dentro del tope. Un valor ilegible es DESCONOCIDO (`null`),
// nunca 0, porque 0 es un precio válido (un juego gratis) y confundirlos inventaría un precio.
function toPriceMinor(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= MAX_CHOSEN_PRICE_MINOR
    ? value
    : null;
}

function toPricingType(value: unknown): CatalogFreshnessPricingType | null {
  const text = toText(value);
  return text !== null && PRICING_TYPE_SET.has(text) ? (text as CatalogFreshnessPricingType) : null;
}

// Solo ISO-8601 con hora y zona. Una fecha sin zona no se puede ubicar en UTC y una cadena arbitraria no es
// una fecha: ambas quedan nulas en vez de llegar al render.
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

function toIsoDateTime(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !ISO_DATE_TIME.test(text)) return null;
  return Number.isNaN(new Date(text).getTime()) ? null : text;
}

/**
 * Fecha que puede usarse como evidencia de observación/consulta. `asOfMs` es el corte del sobre: una fecha
 * posterior al corte (más la tolerancia de relojes) no se acepta, porque calificaría como fresca una fila que
 * todavía no existió. Con el corte desconocido no hay contra qué comparar y la fecha se conserva.
 */
function toEvidenceDateTime(value: unknown, asOfMs: number | null): string | null {
  const text = toIsoDateTime(value);
  if (text === null || asOfMs === null) return text;
  return new Date(text).getTime() > asOfMs + FRESHNESS_FUTURE_TOLERANCE_MS ? null : text;
}

// Portada externa: solo https absoluto, se conserva tal cual (sin reescrituras).
function toHttpsUrl(value: unknown): string | null {
  if (typeof value !== "string" || !value) return null;
  try {
    return new URL(value).protocol === "https:" ? value : null;
  } catch {
    return null;
  }
}

function normalizeProviders(value: unknown, asOfMs: number | null): CatalogFreshnessProvider[] {
  if (!Array.isArray(value)) return [];

  const providers: CatalogFreshnessProvider[] = [];
  const seen = new Set<string>();
  for (const entry of value) {
    if (!isRecord(entry)) continue;
    const provider = toBoundedText(read(entry, "provider"), MAX_PROVIDER_TEXT);
    if (provider === null) continue;
    // Un mismo proveedor dos veces inflaría el conteo de pendientes: gana la primera aparición.
    if (seen.has(provider)) continue;
    seen.add(provider);
    providers.push({
      provider,
      priceObservedAtUtc: toEvidenceDateTime(read(entry, "priceObservedAtUtc"), asOfMs),
      lastCheckedAtUtc: toEvidenceDateTime(read(entry, "lastCheckedAtUtc"), asOfMs)
    });
    if (providers.length === MAX_FRESHNESS_PROVIDERS) break;
  }

  return providers;
}

// Los cuatro campos del ganador se leen del MISMO registro de la fila. Si el monto es ilegible no hay oferta
// ganadora que pintar (la tienda y el tipo sueltos no son un precio), así que la tarjeta dirá que falta el monto
// en vez de juntar el nombre de una tienda con un monto de otra.
function normalizeChosenOffer(
  value: Record<string, unknown>,
  status: CatalogFreshnessStatus
): CatalogFreshnessChosenOffer | null {
  // `missing` niega tener precio comparable: un monto junto a ese estado es contradictorio y se descarta.
  if (status === "missing") return null;

  const priceMinor = toPriceMinor(read(value, "chosenPriceMinor"));
  if (priceMinor === null) return null;

  const pricingType = toPricingType(read(value, "chosenPricingType"));
  return {
    priceMinor,
    shopName: toBoundedText(read(value, "chosenShopName"), MAX_CHOSEN_SHOP_NAME),
    source: toBoundedText(read(value, "chosenSource"), MAX_PROVIDER_TEXT),
    pricingType,
    usesFxEstimate: pricingType === "fx_estimate"
  };
}

function normalizeItem(value: unknown, asOfMs: number | null): CatalogFreshnessItem | null {
  if (!isRecord(value)) return null;

  const gameId = toPositiveInteger(read(value, "gameId"));
  const name = toBoundedText(read(value, "name"), MAX_NAME_TEXT);
  const rawStatus = toText(read(value, "status"));

  if (gameId === null || name === null || rawStatus === null || !STATUS_SET.has(rawStatus)) return null;
  const status = rawStatus as CatalogFreshnessStatus;

  // `recent`/`stale` son afirmaciones sobre una fecha: sin una fecha válida no hay nada que afirmar y la fila
  // se descarta (no se adivina a qué cubeta pertenece). Un juego `missing` no puede exhibir fecha de precio,
  // porque esa fecha sería el precio comparable que su estado niega.
  const priceObservedAtUtc = status === "missing" ? null : toEvidenceDateTime(read(value, "priceObservedAtUtc"), asOfMs);
  if (status !== "missing" && priceObservedAtUtc === null) return null;

  return {
    gameId,
    name,
    imageUrl: toHttpsUrl(read(value, "imageUrl")),
    steamAppId: toPositiveInteger(read(value, "steamAppId")),
    status,
    priceObservedAtUtc,
    chosenOffer: normalizeChosenOffer(value, status),
    providers: normalizeProviders(read(value, "providers"), asOfMs)
  };
}

function normalizeCount(value: unknown): number | null {
  return toNonNegativeInteger(value);
}

// Parámetro de filtro reenviado por el BFF: el token exacto (recortado y en minúsculas) o nada. Un valor
// desconocido no se convierte en «all»: el BFF responde 400 antes de llamar al backend.
export function parseCatalogFreshnessFilterParam(value: string | null): CatalogFreshnessFilter | null {
  if (value === null) return null;
  const normalized = value.trim().toLowerCase();
  return FILTER_SET.has(normalized) ? (normalized as CatalogFreshnessFilter) : null;
}

// Parámetro de paginación: solo un entero positivo dentro de la cota. Cualquier otra cosa se descarta para que
// el backend aplique su propio default en vez de recibir basura.
export function parseCatalogFreshnessPageParam(value: string | null): number | null {
  if (value === null || !/^[1-9]\d*$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 1 && parsed <= CATALOG_FRESHNESS_PAGE_MAX ? parsed : null;
}

// Cualquier sobre que lleve el contador aparte (la página o el resumen que el panel conserva entre errores).
export type UnresolvedWishlistEntryCount = {
  readonly unresolvedWishlistEntries: number | null;
};

// Entradas de la lista de deseos sin identidad reconocida. `null` es DESCONOCIDO (nunca 0): la ausencia del
// campo aditivo no prueba que no haya entradas sin identificar.
export function unresolvedWishlistEntriesCount(
  source: UnresolvedWishlistEntryCount | null
): number | null {
  return source?.unresolvedWishlistEntries ?? null;
}

// El aviso depende SOLO del contador aparte: con 0 juegos canónicos y N entradas sin identificar > 0 el panel
// tiene que seguir avisando. Un 0 conocido o un desconocido no inventan aviso.
export function hasUnresolvedWishlistEntries(source: UnresolvedWishlistEntryCount | null): boolean {
  const count = unresolvedWishlistEntriesCount(source);
  return count !== null && count > 0;
}

// Cuántos juegos declara un filtro en un juego de contadores ya normalizado. Los cuatro contadores llegan
// juntos en el mismo sobre, así que esto nunca dispara un pedido.
export function freshnessCountFromCounts(
  counts: CatalogFreshnessCounts | null,
  filter: CatalogFreshnessFilter
): number | null {
  if (counts === null) return null;
  switch (filter) {
    case "all":
      return counts.total;
    case "recent":
      return counts.recent;
    case "stale":
      return counts.stale;
    default:
      return counts.missing;
  }
}

// Igual que `freshnessCountFromCounts`, sobre el sobre completo: los contadores de un filtro no dependen de la
// página seleccionada.
export function catalogFreshnessCount(
  page: CatalogFreshnessPage | null,
  filter: CatalogFreshnessFilter
): number | null {
  return freshnessCountFromCounts(page?.counts ?? null, filter);
}

/**
 * Normaliza el sobre del backend. `requested` es el filtro que el cliente pidió y es la única fuente del
 * `filter` resultante: una respuesta atrasada que dice otro filtro no puede renombrar la pestaña activa.
 *
 * Un payload vacío, roto o parcial degrada a una página vacía con contadores desconocidos: nunca lanza,
 * porque un error de forma no es un error de red y la página tiene que seguir mostrando sus controles.
 */
export function normalizeCatalogFreshnessPage(input: unknown, requested: CatalogFreshnessFilter): CatalogFreshnessPage {
  const record = isRecord(input) ? input : {};
  const asOfUtc = toIsoDateTime(read(record, "asOfUtc"));
  const asOfMs = asOfUtc === null ? null : new Date(asOfUtc).getTime();

  const rawItems = read(record, "items");
  const items: CatalogFreshnessItem[] = [];
  const seen = new Set<number>();
  if (Array.isArray(rawItems)) {
    for (const entry of rawItems) {
      const item = normalizeItem(entry, asOfMs);
      if (item === null) continue;
      // Un juego canónico aparece una sola vez: un duplicado conserva la primera aparición (el orden del
      // servidor).
      if (seen.has(item.gameId)) continue;
      seen.add(item.gameId);
      items.push(item);
      if (items.length === MAX_CATALOG_FRESHNESS_ITEMS) break;
    }
  }

  const count = items.length;
  const total = Math.max(toNonNegativeInteger(read(record, "total")) ?? count, count);

  const pageSize = toNonNegativeInteger(read(record, "pageSize"));
  const pageNumber = toNonNegativeInteger(read(record, "page"));
  const page = pageNumber === null || pageNumber < 1 ? 1 : Math.min(pageNumber, CATALOG_FRESHNESS_PAGE_MAX);
  const boundedPageSize =
    pageSize === null || pageSize < 1
      ? CATALOG_FRESHNESS_PAGE_SIZE_DEFAULT
      : Math.min(pageSize, CATALOG_FRESHNESS_PAGE_SIZE_MAX);

  const rawCounts = read(record, "counts");
  const countsRecord = isRecord(rawCounts) ? rawCounts : {};
  const recent = normalizeCount(read(countsRecord, "recent"));
  const stale = normalizeCount(read(countsRecord, "stale"));
  const missing = normalizeCount(read(countsRecord, "missing"));
  // Las tres cubetas particionan la lista de deseos: el total nunca puede dejarlas fuera. Si el total del servidor no
  // se puede leer, la suma de las cubetas legibles es la única cifra honesta (y si ninguna se lee, desconocido).
  const bucketSum = (recent ?? 0) + (stale ?? 0) + (missing ?? 0);
  const totalCount = normalizeCount(read(countsRecord, "total"));
  const countsTotal =
    totalCount === null ? (bucketSum > 0 ? bucketSum : null) : Math.max(totalCount, bucketSum);

  const freshnessHours = toPositiveInteger(read(record, "freshnessHours")) ?? CATALOG_FRESHNESS_HOURS;

  return {
    filter: requested,
    asOfUtc,
    freshnessHours,
    page,
    pageSize: boundedPageSize,
    total,
    counts: { total: countsTotal, recent, stale, missing },
    unresolvedWishlistEntries: normalizeCount(read(record, "unresolvedWishlistEntries")),
    items,
    count,
    hasMore: page * boundedPageSize < total
  };
}
