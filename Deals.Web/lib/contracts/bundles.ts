// Contrato del catálogo de bundles (`GET /api/bundles`). Datos de solo lectura sobre bundles ITAD ya
// observados: ningún precio de aquí es una oferta ni entra en el comparador. `allocatedPerGameMinor` es
// un reparto estimado que exige comprar el tier completo, y solo existe con `allocationStatus === "ok"`.
// El catálogo llega paginado (`page`/`pageSize`/`total`/`hasMore`) y marcado con los juegos de wishlist
// del usuario autenticado (`hasWishlistMatch`/`wishlistMatches`): la coincidencia nunca usa títulos.

export const BUNDLE_ALLOCATION_STATUSES = [
  "ok",
  "addon",
  "items_incomplete",
  "no_tier_price",
  "currency_unknown",
  "no_eligible_items"
] as const;

export type BundleAllocationStatus = (typeof BUNDLE_ALLOCATION_STATUSES)[number];

// Vocabulario del precio ya derivado en el servidor. `regional` es el precio regional persistido,
// `fx_estimate` una conversión con la tasa persistida y `unconverted` un precio que no se pudo
// convertir. La UI nunca convierte: solo valida la forma y etiqueta la cifra recibida.
export const BUNDLE_PRICING_TYPES = ["regional", "fx_estimate", "unconverted"] as const;

export type BundlePricingType = (typeof BUNDLE_PRICING_TYPES)[number];

// Bloque de dinero dual del servidor: las dos monedas que la página muestra (USD y MXN) y la tasa
// persistida que respalda la conversión. Es aditivo y opcional: un payload viejo sin el bloque deja
// los dos importes en null —nunca en 0— y la UI simplemente no pinta filas duales.
export type BundleMoney = {
  readonly usdMinor: number | null;
  readonly mxnMinor: number | null;
  /**
   * Tasa de la fila persistida explícita que usó el servidor, en la dirección que fija `currency` del
   * tier: USD nativo ⇒ MXN por USD (p. ej. 18.2); MXN nativo ⇒ fila MXN/USD (p. ej. 0.055 USD por MXN).
   * No es siempre «MXN por USD» y el cliente no debe invertirla: el servidor no sintetiza inversas.
   */
  readonly fxRate: number | null;
  readonly fxRateDate: string | null;
  readonly fxSource: string | null;
  // `null` cuando el servidor no lo mandó o mandó algo fuera del vocabulario: la UI no afirma entonces
  // una procedencia que no conoce.
  readonly pricingType: BundlePricingType | null;
};

export type BundleCatalogGame = {
  readonly appId: number;
  readonly name: string;
  readonly imageUrl: string | null;
};

export type BundleWishlistMatch = {
  readonly appId: number;
  readonly name: string;
};

export type BundleCatalogTier = {
  readonly priceMinor: number | null;
  readonly currency: string | null;
  readonly addon: boolean;
  readonly itemsComplete: boolean;
  readonly itemCount: number;
  readonly eligibleGameCount: number;
  readonly allocatedPerGameMinor: number | null;
  readonly allocationStatus: BundleAllocationStatus;
  readonly requiresWholeTierPurchase: boolean;
  readonly selectionCount: number | null;
  // Precio del tier y reparto por juego en USD/MXN. Opcionales (compatibilidad con un payload viejo) y
  // solo de presentación: no sustituyen a `priceMinor`/`allocatedPerGameMinor` ni entran en el comparador.
  readonly priceMoney: BundleMoney | null;
  readonly allocatedPerGameMoney: BundleMoney | null;
};

export type BundleCatalogItem = {
  readonly source: string;
  readonly bundleKey: string;
  readonly title: string;
  readonly shopName: string | null;
  readonly pageUrl: string | null;
  readonly dealUrl: string | null;
  readonly details: string | null;
  // `detailsUrl` solo existe cuando `details` ES una URL https completa e íntegra: es el único valor
  // que puede llegar a un `href`. Texto libre, otro esquema, espacios o un texto truncado quedan en null
  // y se pintan como texto.
  readonly detailsUrl: string | null;
  readonly publishedAt: string | null;
  readonly expiresAt: string | null;
  readonly observedAt: string | null;
  readonly linkedGames: readonly BundleCatalogGame[];
  readonly tiers: readonly BundleCatalogTier[];
  readonly hasWishlistMatch: boolean;
  readonly wishlistMatches: readonly BundleWishlistMatch[];
};

export type BundleCatalog = {
  readonly items: readonly BundleCatalogItem[];
  readonly count: number;
  readonly total: number;
  readonly page: number;
  readonly pageSize: number;
  readonly hasMore: boolean;
};

// Topes del contrato, iguales a los del backend. Acotan el render y la respuesta ante un payload ajeno.
export const MAX_BUNDLES = 50;
export const MAX_LINKED_GAMES = 20;
export const MAX_TIERS = 20;
export const MAX_WISHLIST_MATCHES = 20;
export const BUNDLE_PAGE_SIZE_DEFAULT = 20;
export const BUNDLE_PAGE_SIZE_MAX = MAX_BUNDLES;

// Identificador de la fuente global de gg.deals. Se usa para la etiqueta de la tarjeta y para no
// presentar su precio como MX: gg.deals no admite la región `mx`.
export const GGDEALS_SOURCE = "ggdeals";
// Región de referencia del proveedor gg.deals (su default documentado). No acredita disponibilidad ni
// precio final para México; si se cambia `GgDeals:Region` en el backend, esta etiqueta debe cambiar.
export const GGDEALS_REFERENCE_REGION = "US";
// Cota defensiva de `page`: evita un offset gigantesco desde un parámetro de query manipulado.
export const BUNDLE_PAGE_MAX = 1_000_000;
const MAX_BUNDLE_TEXT = 512;
const MAX_DETAILS_TEXT = 600;

const ALLOCATION_STATUS_SET = new Set<string>(BUNDLE_ALLOCATION_STATUSES);

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

function toPositiveInteger(value: unknown): number | null {
  const parsed = toNonNegativeInteger(value);
  return parsed !== null && parsed > 0 ? parsed : null;
}

// Solo ISO-8601 con hora y zona; cualquier otra cosa se descarta (null) en vez de llegar al render.
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

function toIsoDateTime(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !ISO_DATE_TIME.test(text)) return null;
  return Number.isNaN(new Date(text).getTime()) ? null : text;
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

function normalizeLinkedGame(value: unknown): BundleCatalogGame | null {
  if (!isRecord(value)) return null;
  const appId = toPositiveInteger(read(value, "appId"));
  const name = toText(read(value, "name"));
  if (appId === null || name === null) return null;
  return { appId, name, imageUrl: toText(read(value, "imageUrl")) };
}

function normalizeWishlistMatch(value: unknown): BundleWishlistMatch | null {
  if (!isRecord(value)) return null;
  const appId = toPositiveInteger(read(value, "appId"));
  const name = toText(read(value, "name"));
  if (appId === null || name === null) return null;
  return { appId, name };
}

function toPositiveDecimal(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

// `fx_rates.rate_date` es una fecha sin hora. Se valida el calendario ("2026-02-30" se convierte en
// marzo) para no mostrar una fecha de tasa que no existe.
function toIsoCalendarDate(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !/^\d{4}-\d{2}-\d{2}$/.test(text)) return null;
  const date = new Date(`${text}T00:00:00Z`);
  return Number.isNaN(date.getTime()) || date.toISOString().slice(0, 10) !== text ? null : text;
}

function toPricingType(value: unknown): BundlePricingType | null {
  const text = toText(value)?.toLowerCase();
  return text === "regional" || text === "fx_estimate" || text === "unconverted" ? text : null;
}

function normalizeMoney(value: unknown): BundleMoney | null {
  // Un arreglo también es "objeto" para `typeof`: se descarta para no pintar un bloque de puros null.
  if (!isRecord(value) || Array.isArray(value)) return null;
  return {
    usdMinor: toNonNegativeInteger(read(value, "usdMinor")),
    mxnMinor: toNonNegativeInteger(read(value, "mxnMinor")),
    fxRate: toPositiveDecimal(read(value, "fxRate")),
    fxRateDate: toIsoCalendarDate(read(value, "fxRateDate")),
    fxSource: toBoundedText(read(value, "fxSource"), 32),
    pricingType: toPricingType(read(value, "pricingType"))
  };
}

// Solo una URL https completa e íntegra se expone como ancla: una frase que menciona un enlace, otro
// esquema (`javascript:`, `data:`), credenciales en la URL o una URL con espacios no se linkifican (el
// linkificado genérico es una inyección con otro nombre). Una URL truncada por el tope de texto tampoco:
// el `href` quedaría roto.
function toDetailsUrl(value: unknown): string | null {
  const text = toText(value);
  if (text === null || text.length > MAX_DETAILS_TEXT || /\s/.test(text)) return null;
  try {
    const url = new URL(text);
    if (url.protocol !== "https:" || !url.hostname || url.username || url.password) return null;
  } catch {
    return null;
  }
  return text;
}

function normalizeTier(value: unknown): BundleCatalogTier | null {
  if (!isRecord(value)) return null;
  const status = toText(read(value, "allocationStatus"));
  if (status === null || !ALLOCATION_STATUS_SET.has(status)) return null;

  const allocated = toNonNegativeInteger(read(value, "allocatedPerGameMinor"));
  // Un estado `ok` sin cifra válida es incoherente: se descarta el tier entero en vez de mostrar un
  // reparto inventado. Un estado no asignable nunca conserva cifra.
  if (status === "ok" && allocated === null) return null;

  const itemCount = toNonNegativeInteger(read(value, "itemCount")) ?? 0;
  const rawSelection = toPositiveInteger(read(value, "selectionCount"));
  // Una regla "elige N de M" mayor que los ítems listados es irresoluble: se descarta en vez de
  // mostrar una proporción inventada.
  const selectionCount = rawSelection !== null && rawSelection <= itemCount ? rawSelection : null;

  return {
    priceMinor: toNonNegativeInteger(read(value, "priceMinor")),
    currency: toBoundedText(read(value, "currency"), 8),
    addon: read(value, "addon") === true,
    itemsComplete: read(value, "itemsComplete") === true,
    itemCount,
    eligibleGameCount: toNonNegativeInteger(read(value, "eligibleGameCount")) ?? 0,
    allocatedPerGameMinor: status === "ok" ? allocated : null,
    allocationStatus: status as BundleAllocationStatus,
    requiresWholeTierPurchase: status === "ok",
    selectionCount,
    priceMoney: normalizeMoney(read(value, "priceMoney")),
    allocatedPerGameMoney: normalizeMoney(read(value, "allocatedPerGameMoney"))
  };
}

function normalizeItem(value: unknown): BundleCatalogItem | null {
  if (!isRecord(value)) return null;
  const title = toBoundedText(read(value, "title"), MAX_BUNDLE_TEXT);
  const source = toBoundedText(read(value, "source"), 16);
  const bundleKey = toBoundedText(
    read(value, "bundleKey") ?? read(value, "providerBundleId") ?? read(value, "id"),
    128
  );
  if (title === null || source === null || bundleKey === null) return null;

  const linkedGames: BundleCatalogGame[] = [];
  const rawGames = read(value, "linkedGames");
  if (Array.isArray(rawGames)) {
    for (const entry of rawGames) {
      const game = normalizeLinkedGame(entry);
      if (game === null) continue;
      linkedGames.push(game);
      if (linkedGames.length === MAX_LINKED_GAMES) break;
    }
  }

  const tiers: BundleCatalogTier[] = [];
  const rawTiers = read(value, "tiers");
  if (Array.isArray(rawTiers)) {
    for (const entry of rawTiers) {
      const tier = normalizeTier(entry);
      if (tier === null) continue;
      tiers.push(tier);
      if (tiers.length === MAX_TIERS) break;
    }
  }

  const wishlistMatches: BundleWishlistMatch[] = [];
  const seenWishlistAppIds = new Set<number>();
  const rawMatches = read(value, "wishlistMatches");
  if (Array.isArray(rawMatches)) {
    for (const entry of rawMatches) {
      const match = normalizeWishlistMatch(entry);
      if (match === null || seenWishlistAppIds.has(match.appId)) continue;
      seenWishlistAppIds.add(match.appId);
      wishlistMatches.push(match);
      if (wishlistMatches.length === MAX_WISHLIST_MATCHES) break;
    }
  }

  return {
    source,
    bundleKey,
    title,
    shopName: toBoundedText(read(value, "shopName"), 128),
    pageUrl: toHttpsUrl(read(value, "pageUrl")),
    dealUrl: toHttpsUrl(read(value, "dealUrl")),
    details: toBoundedText(read(value, "details"), MAX_DETAILS_TEXT),
    detailsUrl: toDetailsUrl(read(value, "details")),
    publishedAt: toIsoDateTime(read(value, "publishedAt")),
    expiresAt: toIsoDateTime(read(value, "expiresAt")),
    observedAt: toIsoDateTime(read(value, "observedAt")),
    linkedGames,
    tiers,
    hasWishlistMatch: read(value, "hasWishlistMatch") === true || wishlistMatches.length > 0,
    wishlistMatches
  };
}

// Parámetro de paginación reenviado por el BFF: solo un entero positivo dentro de la cota. Cualquier
// otra cosa se descarta para que el backend aplique su default en vez de recibir basura.
export function parseBundlePageParam(value: string | null): number | null {
  if (value === null || !/^[1-9]\d*$/.test(value)) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 1 && parsed <= BUNDLE_PAGE_MAX ? parsed : null;
}

export function normalizeBundleCatalog(input: unknown): BundleCatalog {
  const record = isRecord(input) ? input : {};
  const rawItems = read(record, "items");

  const items: BundleCatalogItem[] = [];
  if (Array.isArray(rawItems)) {
    for (const entry of rawItems) {
      const item = normalizeItem(entry);
      if (item === null) continue;
      items.push(item);
      if (items.length === MAX_BUNDLES) break;
    }
  }

  const pageSize = toPositiveInteger(read(record, "pageSize"));

  return {
    items,
    count: toNonNegativeInteger(read(record, "count")) ?? items.length,
    // Un payload viejo (sin total) conserva el respaldo local; nunca se inventa un total mayor.
    total: toNonNegativeInteger(read(record, "total")) ?? items.length,
    page: Math.min(toPositiveInteger(read(record, "page")) ?? 1, BUNDLE_PAGE_MAX),
    pageSize: Math.min(pageSize ?? BUNDLE_PAGE_SIZE_DEFAULT, BUNDLE_PAGE_SIZE_MAX),
    hasMore: read(record, "hasMore") === true
  };
}
