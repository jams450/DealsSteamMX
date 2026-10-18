// Contrato del feed global de ofertas (`GET /api/deals`). Solo lectura sobre ofertas individuales de
// ITAD ya observadas por el barrido de fondo: ningún monto de aquí es una oferta en `game_offers` ni
// entra en el comparador. `currency` es SIEMPRE la moneda original del proveedor; `mxnCurrentPriceMinor`
// solo existe con `pricingType === "regional" | "fx_estimate"`, y el segundo es una estimación por tipo
// de cambio. No hay nota ni rating: `goodDealReason` es un código copiado de campos reales.

export const GLOBAL_DEAL_PRICING_TYPES = ["regional", "fx_estimate", "unconverted"] as const;
export type GlobalDealPricingType = (typeof GLOBAL_DEAL_PRICING_TYPES)[number];

export const GLOBAL_DEAL_REASONS = ["discount", "history_low"] as const;
export type GlobalDealReason = (typeof GLOBAL_DEAL_REASONS)[number];

export type GlobalDealItem = {
  readonly dealKey: string;
  readonly itadGameId: string;
  readonly gameTitle: string;
  readonly gameSlug: string | null;
  readonly gameType: string | null;
  readonly gameMature: boolean;
  readonly shopId: string;
  readonly shopName: string;
  readonly currency: string;
  readonly currentPriceMinor: number;
  readonly regularPriceMinor: number | null;
  readonly discountPercent: number | null;
  readonly mxnCurrentPriceMinor: number | null;
  readonly pricingType: GlobalDealPricingType;
  readonly fxRate: number | null;
  readonly fxRateDate: string | null;
  readonly fxSource: string | null;
  readonly historyLowMinor: number | null;
  readonly historyLowCurrency: string | null;
  readonly isAtHistoryLow: boolean;
  readonly goodDealReason: GlobalDealReason | null;
  readonly dealUrl: string | null;
  readonly expiresAt: string | null;
  readonly observedAt: string | null;
};

export type GlobalDealCatalog = {
  readonly items: readonly GlobalDealItem[];
  readonly count: number;
  readonly total: number;
  readonly page: number;
  readonly pageSize: number;
  readonly hasMore: boolean;
};

// Topes del contrato, iguales a los del backend. Acotan el render y la respuesta ante un payload ajeno.
export const MAX_GLOBAL_DEALS = 50;
export const GLOBAL_DEALS_PAGE_SIZE_DEFAULT = 20;
export const GLOBAL_DEALS_PAGE_SIZE_MAX = MAX_GLOBAL_DEALS;
// Cota defensiva de `page`: evita un offset gigantesco desde un parámetro de query manipulado.
export const GLOBAL_DEALS_PAGE_MAX = 1_000_000;
const MAX_TITLE_TEXT = 512;
const MAX_SLUG_TEXT = 256;

const PRICING_TYPE_SET = new Set<string>(GLOBAL_DEAL_PRICING_TYPES);
const REASON_SET = new Set<string>(GLOBAL_DEAL_REASONS);

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
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

function toFiniteNumber(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

// Solo ISO-8601 con hora y zona; cualquier otra cosa se descarta (null) en vez de llegar al render.
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

function toIsoDateTime(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !ISO_DATE_TIME.test(text)) return null;
  return Number.isNaN(new Date(text).getTime()) ? null : text;
}

function toIsoDate(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !ISO_DATE.test(text)) return null;
  return Number.isNaN(new Date(`${text}T00:00:00Z`).getTime()) ? null : text;
}

// Enlace externo: solo https absoluto. Se conserva tal cual (sin reescrituras) para no alterar el tag
// de afiliado de ITAD.
function toHttpsUrl(value: unknown): string | null {
  if (typeof value !== "string" || !value) return null;
  try {
    return new URL(value).protocol === "https:" ? value : null;
  } catch {
    return null;
  }
}

function normalizeItem(value: unknown): GlobalDealItem | null {
  if (!isRecord(value)) return null;

  const dealKey = toBoundedText(read(value, "dealKey"), 128);
  const itadGameId = toBoundedText(read(value, "itadGameId"), 36);
  const gameTitle = toBoundedText(read(value, "gameTitle"), MAX_TITLE_TEXT);
  const shopId = toBoundedText(read(value, "shopId"), 32);
  const shopName = toBoundedText(read(value, "shopName"), 128);
  const currency = toBoundedText(read(value, "currency"), 3)?.toUpperCase() ?? null;
  const currentPriceMinor = toNonNegativeInteger(read(value, "currentPriceMinor"));
  const pricingType = toText(read(value, "pricingType"));

  if (
    dealKey === null ||
    itadGameId === null ||
    gameTitle === null ||
    shopId === null ||
    shopName === null ||
    currency === null ||
    currentPriceMinor === null ||
    pricingType === null ||
    !PRICING_TYPE_SET.has(pricingType)
  ) {
    return null;
  }

  const rawReason = toText(read(value, "goodDealReason"));
  const goodDealReason = rawReason !== null && REASON_SET.has(rawReason)
    ? (rawReason as GlobalDealReason)
    : null;

  const discountPercent = toNonNegativeInteger(read(value, "discountPercent"));
  // Un MXN presente con una clasificación sin conversión sería incoherente: se descarta la cifra, no se
  // inventa la clasificación.
  const mxnCurrentPriceMinor =
    pricingType === "unconverted" ? null : toNonNegativeInteger(read(value, "mxnCurrentPriceMinor"));

  return {
    dealKey,
    itadGameId,
    gameTitle,
    gameSlug: toBoundedText(read(value, "gameSlug"), MAX_SLUG_TEXT),
    gameType: toBoundedText(read(value, "gameType"), 32),
    gameMature: read(value, "gameMature") === true,
    shopId,
    shopName,
    currency,
    currentPriceMinor,
    regularPriceMinor: toNonNegativeInteger(read(value, "regularPriceMinor")),
    discountPercent: discountPercent !== null && discountPercent <= 100 ? discountPercent : null,
    mxnCurrentPriceMinor,
    pricingType: pricingType as GlobalDealPricingType,
    fxRate: toFiniteNumber(read(value, "fxRate")),
    fxRateDate: toIsoDate(read(value, "fxRateDate")),
    fxSource: toBoundedText(read(value, "fxSource"), 32),
    historyLowMinor: toNonNegativeInteger(read(value, "historyLowMinor")),
    historyLowCurrency: toBoundedText(read(value, "historyLowCurrency"), 3)?.toUpperCase() ?? null,
    isAtHistoryLow: read(value, "isAtHistoryLow") === true,
    goodDealReason,
    dealUrl: toHttpsUrl(read(value, "dealUrl")),
    expiresAt: toIsoDateTime(read(value, "expiresAt")),
    observedAt: toIsoDateTime(read(value, "observedAt"))
  };
}

// Parámetro de paginación reenviado por el BFF: solo un entero positivo dentro de la cota. Cualquier
// otra cosa se descarta para que el backend aplique su default en vez de recibir basura.
export function parseGlobalDealPageParam(value: string | null): number | null {
  if (value === null || !/^[1-9]\d*$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 1 && parsed <= GLOBAL_DEALS_PAGE_MAX ? parsed : null;
}

export function normalizeGlobalDealCatalog(input: unknown): GlobalDealCatalog {
  const record = isRecord(input) ? input : {};
  const rawItems = read(record, "items");

  const items: GlobalDealItem[] = [];
  if (Array.isArray(rawItems)) {
    for (const entry of rawItems) {
      const item = normalizeItem(entry);
      if (item === null) continue;
      items.push(item);
      if (items.length === MAX_GLOBAL_DEALS) break;
    }
  }

  const pageSize = toNonNegativeInteger(read(record, "pageSize"));

  return {
    items,
    count: toNonNegativeInteger(read(record, "count")) ?? items.length,
    // Un payload viejo (sin total) conserva el respaldo local; nunca se inventa un total mayor.
    total: toNonNegativeInteger(read(record, "total")) ?? items.length,
    page: Math.min(toNonNegativeInteger(read(record, "page")) ?? 1, GLOBAL_DEALS_PAGE_MAX),
    pageSize: Math.min(pageSize ?? GLOBAL_DEALS_PAGE_SIZE_DEFAULT, GLOBAL_DEALS_PAGE_SIZE_MAX),
    hasMore: read(record, "hasMore") === true
  };
}
