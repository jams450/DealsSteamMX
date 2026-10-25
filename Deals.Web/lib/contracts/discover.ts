// Contrato de `/discover` (spec S1/S2/S3/S6). Una sola llamada (`GET /api/bff/discover?category&page&pageSize`)
// devuelve la página de UNA categoría junto con los tres contadores de pestaña, así que las pestañas nunca
// hacen tres pedidos para pintar sus números.
//
// Las tres categorías son las tres reglas independientes de `DealAlertPolicy` en el backend (`under50`,
// `historic`, `score`), no una escala ni un embudo: un juego de 150 MXN puede estar en `score`, y uno de 60
// MXN puede estar en `historic`. No existe `discount` ni `recent`.
//
// Cada tarjeta es la oferta que calificó en esa categoría: `shopName`, `classification`, `pricingType`,
// `mxnPriceMinor`, `historyLowMinor` y `dealUrl` vienen de la MISMA fila persistida, así que una tarjeta no
// puede pintar el nombre de una tienda sobre el precio o el enlace de otra. `steamAppId` es nullable y NUNCA
// se deriva de `gameId`: un juego que Steam no vende (Epic, Microsoft, solo keyshop) no tiene ficha interna.

export const DISCOVER_CATEGORIES = ["under50", "historic", "score"] as const;
export type DiscoverCategory = (typeof DISCOVER_CATEGORIES)[number];

// Motivos de la política compartida (`DealAlertReasons`). Son códigos de máquina; el copy vive en la UI.
export const DISCOVER_REASONS = ["price", "score", "history_low"] as const;
export type DiscoverReason = (typeof DISCOVER_REASONS)[number];

// `unconverted` no llega al catálogo (el backend lo excluye): un monto sin convertir no es precio MXN.
// Se valida igual porque la etiqueta de una cifra (`≈` o precio regional) depende de este campo.
export const DISCOVER_PRICING_TYPES = ["regional", "fx_estimate"] as const;
export type DiscoverPricingType = (typeof DISCOVER_PRICING_TYPES)[number];

export const DISCOVER_CLASSIFICATIONS = ["official", "authorized", "keyshop"] as const;
export type DiscoverClassification = (typeof DISCOVER_CLASSIFICATIONS)[number];

export type DiscoverItem = {
  readonly gameId: number;
  readonly steamAppId: number | null;
  readonly title: string;
  readonly imageUrl: string | null;
  readonly source: string;
  readonly shopName: string;
  readonly classification: DiscoverClassification | null;
  readonly pricingType: DiscoverPricingType;
  readonly mxnPriceMinor: number;
  readonly originalPriceMinor: number | null;
  readonly originalCurrency: string;
  // Derivado de `pricingType`, nunca del booleano del payload: `fx_estimate` SIEMPRE se marca como estimado.
  readonly usesFxEstimate: boolean;
  readonly historyLowMinor: number | null;
  readonly historyLowCurrency: string | null;
  readonly dealUrl: string | null;
  readonly score: number | null;
  readonly reasons: readonly DiscoverReason[];
  readonly observedAt: string | null;
};

export type DiscoverPage = {
  readonly category: DiscoverCategory;
  readonly items: readonly DiscoverItem[];
  // Cuántas tarjetas pinta ESTA página (después de descartar payload inválido), no lo que el sobre afirmó.
  readonly count: number;
  // Cuántos juegos tiene la categoría seleccionada en el catálogo persistido.
  readonly total: number;
  readonly under50Total: number;
  readonly historicTotal: number;
  readonly scoreTotal: number;
  readonly page: number;
  readonly pageSize: number;
  readonly hasMore: boolean;
};

// Topes del contrato, iguales a `CatalogDealPager` del backend. Acotan el render y el offset de una query
// manipulada.
export const DISCOVER_PAGE_SIZE_DEFAULT = 20;
export const DISCOVER_PAGE_SIZE_MAX = 50;
export const DISCOVER_PAGE_MAX = 1_000_000;
export const MAX_DISCOVER_ITEMS = DISCOVER_PAGE_SIZE_MAX;

const MAX_TITLE_TEXT = 512;
const MAX_TEXT = 128;
const MAX_SOURCE_TEXT = 32;

const CATEGORY_SET = new Set<string>(DISCOVER_CATEGORIES);
const REASON_SET = new Set<string>(DISCOVER_REASONS);
const PRICING_TYPE_SET = new Set<string>(DISCOVER_PRICING_TYPES);
const CLASSIFICATION_SET = new Set<string>(DISCOVER_CLASSIFICATIONS);

export function isDiscoverCategory(value: unknown): value is DiscoverCategory {
  return typeof value === "string" && CATEGORY_SET.has(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

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

// Una nota es una cifra de la fórmula 0-10: una cadena no es una nota, y fuera de rango no es una nota.
function toScore(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= 10 ? value : null;
}

function toCurrency(value: unknown): string | null {
  const text = toText(value);
  return text !== null && /^[A-Za-z]{3}$/.test(text) ? text.toUpperCase() : null;
}

// Solo ISO-8601 con hora y zona; cualquier otra cosa queda nula en vez de llegar al render.
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

function toIsoDateTime(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !ISO_DATE_TIME.test(text)) return null;
  return Number.isNaN(new Date(text).getTime()) ? null : text;
}

// Enlace e imagen externos: solo https absoluto. Se conserva tal cual (sin reescrituras) para no alterar el
// tag de afiliado del proveedor, y una URL relativa o de otro esquema no se convierte en enlace.
function toHttpsUrl(value: unknown): string | null {
  if (typeof value !== "string" || !value) return null;
  try {
    return new URL(value).protocol === "https:" ? value : null;
  } catch {
    return null;
  }
}

function normalizeReasons(value: unknown): DiscoverReason[] {
  if (!Array.isArray(value)) return [];
  const reasons: DiscoverReason[] = [];
  for (const entry of value) {
    if (typeof entry !== "string" || !REASON_SET.has(entry)) continue;
    const reason = entry as DiscoverReason;
    if (!reasons.includes(reason)) reasons.push(reason);
  }
  return reasons;
}

function normalizeItem(value: unknown): DiscoverItem | null {
  if (!isRecord(value)) return null;

  const gameId = toPositiveInteger(read(value, "gameId"));
  const title = toBoundedText(read(value, "title"), MAX_TITLE_TEXT);
  const source = toBoundedText(read(value, "source"), MAX_SOURCE_TEXT);
  const shopName = toBoundedText(read(value, "shopName"), MAX_TEXT);
  const pricingType = toText(read(value, "pricingType"));
  const mxnPriceMinor = toPositiveInteger(read(value, "mxnPriceMinor"));
  const originalCurrency = toCurrency(read(value, "originalCurrency"));

  if (
    gameId === null ||
    title === null ||
    source === null ||
    shopName === null ||
    pricingType === null ||
    !PRICING_TYPE_SET.has(pricingType) ||
    mxnPriceMinor === null ||
    originalCurrency === null
  ) {
    return null;
  }

  const rawClassification = toText(read(value, "classification"));
  const historyLowMinor = toNonNegativeInteger(read(value, "historyLowMinor"));
  const historyLowCurrency = toCurrency(read(value, "historyLowCurrency"));
  // Un mínimo sin su moneda (o al revés) no es evidencia de mínimo: se descartan los dos.
  const hasHistoryLow = historyLowMinor !== null && historyLowMinor > 0 && historyLowCurrency !== null;

  return {
    gameId,
    // Solo un entero positivo es un appid de Steam; 0, un negativo o una cadena no se convierten en ficha.
    steamAppId: toPositiveInteger(read(value, "steamAppId")),
    title,
    imageUrl: toHttpsUrl(read(value, "imageUrl")),
    source,
    shopName,
    classification:
      rawClassification !== null && CLASSIFICATION_SET.has(rawClassification)
        ? (rawClassification as DiscoverClassification)
        : null,
    pricingType: pricingType as DiscoverPricingType,
    mxnPriceMinor,
    originalPriceMinor: toNonNegativeInteger(read(value, "originalPriceMinor")),
    originalCurrency,
    usesFxEstimate: pricingType === "fx_estimate",
    historyLowMinor: hasHistoryLow ? historyLowMinor : null,
    historyLowCurrency: hasHistoryLow ? historyLowCurrency : null,
    dealUrl: toHttpsUrl(read(value, "dealUrl")),
    score: toScore(read(value, "score")),
    reasons: normalizeReasons(read(value, "reasons")),
    observedAt: toIsoDateTime(read(value, "observedAt"))
  };
}

// Parámetro de categoría reenviado por el BFF: el token exacto (recortado y en minúsculas) o nada. Un token
// desconocido no se convierte en la primera categoría: el BFF responde 400 antes de llamar al backend.
export function parseDiscoverCategoryParam(value: string | null): DiscoverCategory | null {
  if (value === null) return null;
  const normalized = value.trim().toLowerCase();
  return CATEGORY_SET.has(normalized) ? (normalized as DiscoverCategory) : null;
}

// Parámetro de paginación reenviado por el BFF: solo un entero positivo dentro de la cota. Cualquier otra
// cosa se descarta para que el backend aplique su propio default en vez de recibir basura.
export function parseDiscoverPageParam(value: string | null): number | null {
  if (value === null || !/^[1-9]\d*$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 1 && parsed <= DISCOVER_PAGE_MAX ? parsed : null;
}

// Cuántos juegos tiene una pestaña según el sobre ya normalizado. Los tres contadores llegan en la misma
// respuesta que la página seleccionada, así que esta función nunca dispara un pedido.
export function discoverCategoryCount(page: DiscoverPage | null, category: DiscoverCategory): number {
  if (page === null) return 0;
  switch (category) {
    case "under50":
      return page.under50Total;
    case "historic":
      return page.historicTotal;
    default:
      return page.scoreTotal;
  }
}

/**
 * Normaliza el sobre del backend. `requested` es la categoría que el cliente pidió y es la única fuente del
 * `category` resultante: una respuesta atrasada que dice otra categoría no puede renombrar la pestaña activa
 * (ni, por tanto, atribuirle a un juego el motivo de otra regla).
 *
 * Un payload vacío, roto o parcial degrada a una página vacía: nunca lanza, porque un error de forma no es un
 * error de red y la página tiene que seguir mostrando sus pestañas.
 */
export function normalizeDiscoverPage(input: unknown, requested: DiscoverCategory): DiscoverPage {
  const record = isRecord(input) ? input : {};
  const rawItems = read(record, "items");

  const items: DiscoverItem[] = [];
  const seen = new Set<number>();
  if (Array.isArray(rawItems)) {
    for (const entry of rawItems) {
      const item = normalizeItem(entry);
      if (item === null) continue;
      // Una sola tarjeta por juego y categoría: un duplicado del mismo `gameId` conserva la primera
      // aparición, que es la que el servidor ordenó por precio.
      if (seen.has(item.gameId)) continue;
      seen.add(item.gameId);
      items.push(item);
      if (items.length === MAX_DISCOVER_ITEMS) break;
    }
  }

  const count = items.length;
  const total = Math.max(toNonNegativeInteger(read(record, "total")) ?? count, count);
  const pageSize = toNonNegativeInteger(read(record, "pageSize"));
  const page = toNonNegativeInteger(read(record, "page"));

  return {
    category: requested,
    items,
    count,
    total,
    under50Total: toNonNegativeInteger(read(record, "under50Total")) ?? count,
    historicTotal: toNonNegativeInteger(read(record, "historicTotal")) ?? count,
    scoreTotal: toNonNegativeInteger(read(record, "scoreTotal")) ?? count,
    page: page === null || page < 1 ? 1 : Math.min(page, DISCOVER_PAGE_MAX),
    pageSize:
      pageSize === null || pageSize < 1
        ? DISCOVER_PAGE_SIZE_DEFAULT
        : Math.min(pageSize, DISCOVER_PAGE_SIZE_MAX),
    hasMore: read(record, "hasMore") === true
  };
}
