// Contrato del catálogo de bundles (`GET /api/bundles`). Datos de solo lectura sobre bundles ITAD ya
// observados: ningún precio de aquí es una oferta ni entra en el comparador. `allocatedPerGameMinor` es
// un reparto estimado que exige comprar el tier completo, y solo existe con `allocationStatus === "ok"`.

export const BUNDLE_ALLOCATION_STATUSES = [
  "ok",
  "addon",
  "items_incomplete",
  "no_tier_price",
  "currency_unknown",
  "no_eligible_items"
] as const;

export type BundleAllocationStatus = (typeof BUNDLE_ALLOCATION_STATUSES)[number];

export type BundleCatalogGame = {
  readonly appId: number;
  readonly name: string;
  readonly imageUrl: string | null;
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
};

export type BundleCatalogItem = {
  readonly source: string;
  readonly bundleKey: string;
  readonly title: string;
  readonly shopName: string | null;
  readonly pageUrl: string | null;
  readonly dealUrl: string | null;
  readonly details: string | null;
  readonly publishedAt: string | null;
  readonly expiresAt: string | null;
  readonly observedAt: string | null;
  readonly linkedGames: readonly BundleCatalogGame[];
  readonly tiers: readonly BundleCatalogTier[];
};

export type BundleCatalog = {
  readonly items: readonly BundleCatalogItem[];
  readonly count: number;
  readonly maxItems: number;
};

// Topes del contrato, iguales a los del backend. Acotan el render y la respuesta ante un payload ajeno.
export const MAX_BUNDLES = 100;
export const MAX_LINKED_GAMES = 20;
export const MAX_TIERS = 20;
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

function normalizeTier(value: unknown): BundleCatalogTier | null {
  if (!isRecord(value)) return null;
  const status = toText(read(value, "allocationStatus"));
  if (status === null || !ALLOCATION_STATUS_SET.has(status)) return null;

  const allocated = toNonNegativeInteger(read(value, "allocatedPerGameMinor"));
  // Un estado `ok` sin cifra válida es incoherente: se descarta el tier entero en vez de mostrar un
  // reparto inventado. Un estado no asignable nunca conserva cifra.
  if (status === "ok" && allocated === null) return null;

  return {
    priceMinor: toNonNegativeInteger(read(value, "priceMinor")),
    currency: toBoundedText(read(value, "currency"), 8),
    addon: read(value, "addon") === true,
    itemsComplete: read(value, "itemsComplete") === true,
    itemCount: toNonNegativeInteger(read(value, "itemCount")) ?? 0,
    eligibleGameCount: toNonNegativeInteger(read(value, "eligibleGameCount")) ?? 0,
    allocatedPerGameMinor: status === "ok" ? allocated : null,
    allocationStatus: status as BundleAllocationStatus,
    requiresWholeTierPurchase: status === "ok"
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

  return {
    source,
    bundleKey,
    title,
    shopName: toBoundedText(read(value, "shopName"), 128),
    pageUrl: toHttpsUrl(read(value, "pageUrl")),
    dealUrl: toHttpsUrl(read(value, "dealUrl")),
    details: toBoundedText(read(value, "details"), MAX_DETAILS_TEXT),
    publishedAt: toIsoDateTime(read(value, "publishedAt")),
    expiresAt: toIsoDateTime(read(value, "expiresAt")),
    observedAt: toIsoDateTime(read(value, "observedAt")),
    linkedGames,
    tiers
  };
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

  return {
    items,
    count: toNonNegativeInteger(read(record, "count")) ?? items.length,
    maxItems: toPositiveInteger(read(record, "maxItems")) ?? MAX_BUNDLES
  };
}
