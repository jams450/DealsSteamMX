// Contrato de compra de bundles (S3-S5): vista previa (`GET /api/bundles/purchases/preview`), confirmación
// (`POST /api/bundles/purchases`), historial (`GET /api/bundles/purchases`), keys duplicadas
// (`GET /api/bundles/purchases/duplicates`) y candidatos del catálogo
// (`GET /api/bundles/catalog-candidates`).
//
// Este módulo es la frontera de forma: el backend es la autoridad de negocio y aquí no se decide nada de
// identidad, propiedad ni reparto. Se valida lo que el servidor puede mandar antes de que un campo raro
// llegue al render, y se arma/valida el único cuerpo que el BFF reenvía. Las reglas de selección (tier
// fijo, cupo exacto, vínculo manual) viven en `app/bundles/_lib/bundle-purchase.ts`, que sí es puro de UI.
//
// Decisiones que el contrato publicado no fija y que aquí se resuelven sin inventar campos:
// - `snapshotHash` debe ser 64 hex. Un hash distinto significa que la vista previa no se puede usar para
//   confirmar: se rechaza entera en vez de mandar un snapshot que el servidor va a rechazar (400).
// - Un ítem sin `itadGameId` y sin `canonicalGameId` exige vínculo manual aunque el servidor diga lo
//   contrario: no hay identidad que resolver, y fabricar una sería el fallo que S4 prohíbe.
// - Un tier con `blockedReason` nunca es confirmable, y un tier de selección sin cupo exacto tampoco:
//   la UI no puede adivinar cuántos juegos importar.
// - El DTO autoritativo del backend manda `shopId` como texto, `acquiredAt` como `DateOnly?` y
//   `existingTitle` como `string?`. Un null es un dato ausente, no una fila inválida: se conserva y la UI
//   lo dice («sin fecha declarada»), nunca lo rellena.
// - El arreglo `duplicates` del resultado de compra ya está publicado, pero esta UI no lo necesita: los
//   conteos publicados mandan y la sección «Keys duplicadas» tiene su propio endpoint. Nada de campos
//   inventados ni de duplicar la verdad en dos respuestas.
import { normalizeStore } from "./stores.ts";

export const PURCHASE_ITEM_CANONICAL_SOURCES = ["itad", "manual"] as const;
export type PurchaseItemCanonicalSource = (typeof PURCHASE_ITEM_CANONICAL_SOURCES)[number];

export const PURCHASE_STORE_TYPES = ["official", "keyshop", "unknown"] as const;
export type PurchaseStoreType = (typeof PURCHASE_STORE_TYPES)[number];

export const PURCHASE_PROVIDERS = ["itad", "ggdeals", "direct", "other"] as const;
export type PurchaseProvider = (typeof PURCHASE_PROVIDERS)[number];

export const TIER_SELECTION_MODES = ["fixed", "choice"] as const;
export type TierSelectionMode = (typeof TIER_SELECTION_MODES)[number];

// Motivos de bloqueo publicados. `invalid_selection_count` también cubre un modo de selección que este
// cliente no entiende: sin saber si hay que elegir juegos, no se puede confirmar nada.
export const TIER_BLOCKED_REASONS = [
  "items_incomplete",
  "addon_tier",
  "no_eligible_games",
  "invalid_selection_count"
] as const;
export type TierBlockedReason = (typeof TIER_BLOCKED_REASONS)[number];

// Topes defensivos: acotan el render y el trabajo ante un payload ajeno o manipulado.
export const MAX_BUNDLE_PURCHASES = 100;
export const MAX_PURCHASE_TIERS = 50;
export const MAX_TIER_ITEMS = 200;
export const MAX_PURCHASE_GAMES = 200;
export const MAX_PURCHASE_DUPLICATES = 500;
export const MAX_PURCHASE_CANDIDATES = 10;
export const CANDIDATE_LIMIT_DEFAULT = 10;
export const CANDIDATE_QUERY_MAX = 120;
export const CANDIDATE_QUERY_MIN = 2;

/** Clave del snapshot del tier: 64 hex, tal como la publica el backend. */
export const SNAPSHOT_HASH_LENGTH = 64;
export const SNAPSHOT_HASH_PATTERN = /^[0-9a-fA-F]{64}$/;

const MAX_IDENTIFIER_LENGTH = 128;
// Identificador opaco del proveedor: letras, números y separadores. Nada de espacios ni texto libre.
const IDENTIFIER_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:-]*$/;
const MAX_TEXT_LENGTH = 512;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
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

// Solo números JSON: un id o un conteo que llega como texto ("9") no se convierte en silencio, porque
// eso aceptaría una respuesta que no respeta el contrato publicado.
function toNonNegativeInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : null;
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

// `acquiredAt` es un día sin hora. Se valida el calendario entero ("2026-02-30" se convierte en marzo).
function toIsoCalendarDate(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !/^\d{4}-\d{2}-\d{2}$/.test(text)) return null;
  const date = new Date(`${text}T00:00:00Z`);
  return Number.isNaN(date.getTime()) || date.toISOString().slice(0, 10) !== text ? null : text;
}

function toHttpsUrl(value: unknown): string | null {
  if (typeof value !== "string" || !value) return null;
  try {
    return new URL(value).protocol === "https:" ? value : null;
  } catch {
    return null;
  }
}

function toIdentifier(value: unknown): string | null {
  const text = toText(value);
  return text !== null && text.length <= MAX_IDENTIFIER_LENGTH && IDENTIFIER_PATTERN.test(text) ? text : null;
}

export function isSnapshotHash(value: unknown): value is string {
  return typeof value === "string" && SNAPSHOT_HASH_PATTERN.test(value);
}

/** Identificador de bundle/tier que viaja por query: misma regla que el del cuerpo. */
export function parseBundleIdentifier(value: string | null | undefined): string | null {
  return toIdentifier(value);
}

function toSnapshotHash(value: unknown): string | null {
  return isSnapshotHash(value) ? value.toLowerCase() : null;
}

// `true`/`false` se conserva; cualquier otra cosa es «no lo sé», nunca un `false` que afirme algo.
function toTriState(value: unknown): boolean | null {
  return value === true ? true : value === false ? false : null;
}

function toVocabulary<T extends string>(value: unknown, vocabulary: readonly T[]): T | null {
  const text = typeof value === "string" ? value : null;
  return text !== null && vocabulary.some((candidate) => candidate === text) ? (text as T) : null;
}

// --- vista previa --------------------------------------------------------

export type BundlePurchasePreviewItem = {
  readonly itemKey: string;
  readonly title: string;
  /** Tipo que reporta ITAD (`game`, `dlc`, …): solo explicativo, nunca decide elegibilidad. */
  readonly type: string | null;
  readonly priceMinor: number | null;
  readonly currency: string | null;
  /** Identidad que ya tiene ITAD. Si existe, el cuerpo manda `canonicalGameId: null`. */
  readonly itadGameId: string | null;
  readonly url: string | null;
  readonly eligible: boolean;
  /** Ficha canónica que el servidor ya conoce (p. ej. un vínculo manual previo). */
  readonly canonicalGameId: number | null;
  readonly canonicalSource: PurchaseItemCanonicalSource | null;
  /**
   * El ítem no tiene identidad resoluble y hay que elegir una ficha del catálogo antes de confirmar.
   * Verdadero también cuando el servidor no lo marca pero tampoco manda ningún id.
   */
  readonly requiresManualLink: boolean;
  readonly owned: boolean | null;
  readonly ownedUserLibraryId: number | null;
  readonly ownedPlatform: string | null;
};

export type BundlePurchasePreviewTier = {
  readonly tierKey: string;
  readonly tierIndex: number;
  readonly priceMinor: number | null;
  readonly currency: string | null;
  readonly addon: boolean;
  readonly itemsComplete: boolean;
  /** Cupo heredado del catálogo: «elige N de M» cuando el tier es de selección. */
  readonly selectionCount: number | null;
  readonly selectionMode: TierSelectionMode;
  /** Número exacto de juegos que hay que seleccionar en un tier `choice`; `null` en un tier fijo. */
  readonly requiredSelectionCount: number | null;
  readonly importable: boolean;
  readonly blockedReason: TierBlockedReason | null;
  readonly items: readonly BundlePurchasePreviewItem[];
};

export type BundlePurchasePreview = {
  readonly source: string;
  readonly bundleKey: string;
  readonly title: string;
  /** Texto opaco del proveedor: el backend lo publica como `string?`, así que no se convierte a número. */
  readonly shopId: string | null;
  readonly shopName: string | null;
  readonly pageUrl: string | null;
  readonly dealUrl: string | null;
  readonly details: string | null;
  readonly publishedAt: string | null;
  readonly expiresAt: string | null;
  readonly observedAt: string | null;
  readonly snapshotHash: string;
  readonly purchased: boolean;
  readonly purchaseId: number | null;
  readonly tiers: readonly BundlePurchasePreviewTier[];
};

function normalizePreviewItem(value: unknown): BundlePurchasePreviewItem | null {
  if (!isRecord(value)) return null;
  const itemKey = toIdentifier(read(value, "itemKey"));
  const title = toBoundedText(read(value, "title"), MAX_TEXT_LENGTH);
  if (itemKey === null || title === null) return null;

  const itadGameId = toBoundedText(read(value, "itadGameId"), 64);
  const canonicalGameId = toPositiveInteger(read(value, "canonicalGameId"));
  const canonicalSource = toVocabulary(read(value, "canonicalSource"), PURCHASE_ITEM_CANONICAL_SOURCES);
  // Un ítem identificado por ITAD nunca pide vínculo: su cuerpo manda `canonicalGameId: null` y el
  // servidor resuelve. Un ítem sin identidad alguna siempre lo pide, diga lo que diga el servidor.
  const requiresManualLink =
    itadGameId === null && (canonicalGameId === null || read(value, "requiresManualLink") === true);

  return {
    itemKey,
    title,
    type: toBoundedText(read(value, "type"), 32),
    priceMinor: toNonNegativeInteger(read(value, "priceMinor")),
    currency: toBoundedText(read(value, "currency"), 8),
    itadGameId,
    url: toHttpsUrl(read(value, "url")),
    eligible: read(value, "eligible") === true,
    canonicalGameId: itadGameId !== null ? null : canonicalGameId,
    canonicalSource: itadGameId !== null ? null : canonicalSource,
    requiresManualLink,
    owned: toTriState(read(value, "owned")),
    ownedUserLibraryId: toPositiveInteger(read(value, "ownedUserLibraryId")),
    ownedPlatform: normalizeStore(read(value, "ownedPlatform"))
  };
}

function normalizePreviewTier(value: unknown, fallbackIndex: number): BundlePurchasePreviewTier | null {
  if (!isRecord(value)) return null;
  const tierKey = toIdentifier(read(value, "tierKey"));
  if (tierKey === null) return null;

  const rawItems = read(value, "items");
  const items: BundlePurchasePreviewItem[] = [];
  let droppedItems = 0;
  if (Array.isArray(rawItems)) {
    for (const entry of rawItems) {
      const item = normalizePreviewItem(entry);
      if (item === null) {
        droppedItems += 1;
        continue;
      }
      items.push(item);
    }
  } else {
    droppedItems += 1;
  }
  // Un tope recortado es una membresía incompleta: se conserva para leerla, pero no se confirma.
  const truncatedItems = items.length > MAX_TIER_ITEMS;
  if (truncatedItems) items.length = MAX_TIER_ITEMS;

  const legacySelectionCount = toPositiveInteger(read(value, "selectionCount"));
  const requiredSelectionCount =
    toPositiveInteger(read(value, "requiredSelectionCount")) ?? legacySelectionCount;
  const rawMode = toVocabulary(read(value, "selectionMode"), TIER_SELECTION_MODES);
  const selectionMode: TierSelectionMode = rawMode ?? "fixed";

  let blockedReason = toVocabulary(read(value, "blockedReason"), TIER_BLOCKED_REASONS);
  let importable = read(value, "importable") === true;

  // Un tier sólo se confirma si de verdad se puede: cualquier contradicción degrada a «no importable» y
  // conserva el motivo publicado en vez de pintar un botón que el servidor va a rechazar. Los motivos que
  // se derivan aquí son hechos del payload (no hay cupo, no hay juegos), no causas inventadas.
  if (blockedReason !== null) importable = false;
  // Un ítem ilegible o recortado es una membresía incompleta: se puede leer, pero no confirmar.
  if (droppedItems > 0 || truncatedItems) importable = false;
  if (rawMode === null) {
    importable = false;
    blockedReason = blockedReason ?? "invalid_selection_count";
  }
  const eligibleCount = items.filter((item) => item.eligible).length;
  if (eligibleCount === 0) {
    importable = false;
    blockedReason = blockedReason ?? "no_eligible_games";
  }
  if (selectionMode === "choice") {
    const resolvable =
      requiredSelectionCount !== null && requiredSelectionCount > 0 && requiredSelectionCount <= eligibleCount;
    if (!resolvable) {
      importable = false;
      blockedReason = blockedReason ?? "invalid_selection_count";
    }
  }

  return {
    tierKey,
    tierIndex: toNonNegativeInteger(read(value, "tierIndex")) ?? fallbackIndex,
    priceMinor: toNonNegativeInteger(read(value, "priceMinor")),
    currency: toBoundedText(read(value, "currency"), 8),
    addon: read(value, "addon") === true,
    itemsComplete: read(value, "itemsComplete") === true,
    selectionCount: legacySelectionCount,
    selectionMode,
    requiredSelectionCount: selectionMode === "choice" ? requiredSelectionCount : null,
    importable,
    blockedReason,
    items
  };
}

/**
 * Vista previa de la compra. Devuelve `null` cuando la respuesta no sirve para confirmar nada: sin
 * snapshot válido, sin identidad de bundle o con más tiers de los que el contrato acota. Un tier
 * contradictorio no invalida la vista previa: se marca no importable con su motivo.
 */
export function normalizeBundlePurchasePreview(input: unknown): BundlePurchasePreview | null {
  if (!isRecord(input)) return null;

  const source = toBoundedText(read(input, "source"), 32);
  const bundleKey = toIdentifier(read(input, "bundleKey"));
  const title = toBoundedText(read(input, "title"), MAX_TEXT_LENGTH);
  const snapshotHash = toSnapshotHash(read(input, "snapshotHash"));
  if (source === null || bundleKey === null || title === null || snapshotHash === null) return null;

  const rawTiers = read(input, "tiers");
  if (!Array.isArray(rawTiers) || rawTiers.length > MAX_PURCHASE_TIERS) return null;

  const tiers: BundlePurchasePreviewTier[] = [];
  for (const [index, entry] of rawTiers.entries()) {
    const tier = normalizePreviewTier(entry, index);
    // Un tier ilegible se descarta: no se puede confirmar lo que no se puede pintar, pero los demás
    // siguen disponibles para el usuario.
    if (tier !== null) tiers.push(tier);
  }

  return {
    source,
    bundleKey,
    title,
    shopId:
      toBoundedText(read(input, "shopId"), 64) ??
      (toPositiveInteger(read(input, "shopId")) !== null ? String(toPositiveInteger(read(input, "shopId"))) : null),
    shopName: toBoundedText(read(input, "shopName"), 128),
    pageUrl: toHttpsUrl(read(input, "pageUrl")),
    dealUrl: toHttpsUrl(read(input, "dealUrl")),
    details: toBoundedText(read(input, "details"), 600),
    publishedAt: toIsoDateTime(read(input, "publishedAt")),
    expiresAt: toIsoDateTime(read(input, "expiresAt")),
    observedAt: toIsoDateTime(read(input, "observedAt")),
    snapshotHash,
    purchased: read(input, "purchased") === true,
    purchaseId: toPositiveInteger(read(input, "purchaseId")),
    tiers
  };
}

// --- cuerpo de la confirmación -------------------------------------------

export type BundlePurchaseRequestItem = {
  readonly itemKey: string;
  /** Override por juego: `null` hereda la plataforma del tier. Siempre una llave canónica de StoreKeys. */
  readonly platform: string | null;
  /** `null` para un ítem identificado por ITAD; id explícito para uno vinculado a mano. */
  readonly canonicalGameId: number | null;
};

export type BundlePurchaseRequestBody = {
  readonly source: string;
  readonly bundleKey: string;
  readonly tierKey: string;
  readonly platform: string;
  readonly snapshotHash: string;
  readonly acquiredAt: string;
  readonly acquiredStore: string;
  readonly acquiredStoreType: PurchaseStoreType;
  readonly acquisitionProvider: PurchaseProvider;
  readonly items: readonly BundlePurchaseRequestItem[];
};

function normalizeRequestItem(value: unknown): BundlePurchaseRequestItem | null {
  if (!isRecord(value)) return null;
  const itemKey = toIdentifier(read(value, "itemKey"));
  if (itemKey === null) return null;

  const rawPlatform = read(value, "platform");
  const normalizedPlatform = rawPlatform === null || rawPlatform === undefined ? null : normalizeStore(rawPlatform);
  // El backend normaliza con StoreKeys, que conoce las tiendas y los slugs de plataforma; aquí se exige que
  // el valor ya venga canónico (misma llave, sin grafías alternativas) para no reescribir la intención.
  if (rawPlatform !== null && rawPlatform !== undefined && normalizedPlatform !== rawPlatform) return null;

  const rawCanonical = read(value, "canonicalGameId");
  const canonicalGameId = toPositiveInteger(rawCanonical);
  if (rawCanonical !== null && rawCanonical !== undefined && canonicalGameId === null) return null;

  return {
    itemKey,
    platform: normalizedPlatform,
    canonicalGameId
  };
}

/**
 * Cuerpo exacto de `POST /api/bundles/purchases`. Devuelve `null` ante cualquier violación del contrato
 * publicado: el BFF lo traduce a 400 sin gastar la llamada. No completa nada por defecto — ni la
 * plataforma, ni la fecha, ni la tienda — porque cada uno de esos campos es un hecho que el usuario
 * declara y el servidor vuelve a validar.
 */
export function normalizeBundlePurchaseRequest(input: unknown): BundlePurchaseRequestBody | null {
  if (!isRecord(input)) return null;

  const source = toIdentifier(read(input, "source"));
  const bundleKey = toIdentifier(read(input, "bundleKey"));
  const tierKey = toIdentifier(read(input, "tierKey"));
  const platform = read(input, "platform");
  const canonicalPlatform = typeof platform === "string" ? normalizeStore(platform) : null;
  const snapshotHash = toSnapshotHash(read(input, "snapshotHash"));
  const acquiredAt = toIsoCalendarDate(read(input, "acquiredAt"));
  const acquiredStore = normalizeStore(read(input, "acquiredStore"));
  const acquiredStoreType = toVocabulary(read(input, "acquiredStoreType"), PURCHASE_STORE_TYPES);
  const acquisitionProvider = toVocabulary(read(input, "acquisitionProvider"), PURCHASE_PROVIDERS);

  if (
    source === null ||
    bundleKey === null ||
    tierKey === null ||
    canonicalPlatform === null ||
    platform !== canonicalPlatform ||
    snapshotHash === null ||
    acquiredAt === null ||
    acquiredStore === null ||
    acquiredStoreType === null ||
    acquisitionProvider === null
  ) {
    return null;
  }

  const rawItems = read(input, "items");
  if (!Array.isArray(rawItems) || rawItems.length === 0 || rawItems.length > MAX_TIER_ITEMS) return null;

  const items: BundlePurchaseRequestItem[] = [];
  const seen = new Set<string>();
  for (const entry of rawItems) {
    const item = normalizeRequestItem(entry);
    if (item === null || seen.has(item.itemKey)) return null;
    seen.add(item.itemKey);
    items.push(item);
  }

  return {
    source,
    bundleKey,
    tierKey,
    platform: canonicalPlatform,
    snapshotHash,
    acquiredAt,
    acquiredStore,
    acquiredStoreType,
    acquisitionProvider,
    items
  };
}

// --- resultado de la compra ----------------------------------------------

export type BundlePurchaseResultGame = {
  readonly itemKey: string;
  readonly title: string;
  readonly gameId: number | null;
  readonly platform: string | null;
  /** Código del servidor. Su vocabulario no está publicado, así que se conserva tal cual. */
  readonly outcome: string | null;
  readonly userLibraryId: number | null;
  readonly canonicalSource: PurchaseItemCanonicalSource | null;
  readonly wishlistRemoved: boolean;
};

export type BundlePurchaseResult = {
  readonly purchaseId: number;
  readonly created: boolean;
  readonly alreadyReported: boolean;
  readonly source: string | null;
  readonly bundleKey: string | null;
  readonly title: string | null;
  readonly tierKey: string | null;
  readonly platform: string | null;
  readonly snapshotHash: string;
  readonly selectedCount: number;
  readonly importedCount: number;
  readonly duplicateCount: number;
  readonly wishlistRemovedCount: number;
  readonly games: readonly BundlePurchaseResultGame[];
};

function normalizeResultGame(value: unknown): BundlePurchaseResultGame | null {
  if (!isRecord(value)) return null;
  const itemKey = toIdentifier(read(value, "itemKey"));
  const title = toBoundedText(read(value, "title"), MAX_TEXT_LENGTH);
  const platform = normalizeStore(read(value, "platform"));
  if (itemKey === null || title === null || platform === null) return null;
  return {
    itemKey,
    title,
    gameId: toPositiveInteger(read(value, "gameId")),
    platform,
    outcome: toBoundedText(read(value, "outcome"), 32),
    userLibraryId: toPositiveInteger(read(value, "userLibraryId")),
    canonicalSource: toVocabulary(read(value, "canonicalSource"), PURCHASE_ITEM_CANONICAL_SOURCES),
    wishlistRemoved: read(value, "wishlistRemoved") === true
  };
}

/**
 * Respuesta de una compra registrada. Solo `purchaseId` y `snapshotHash` son obligatorios: la compra ya
 * ocurrió, así que un campo de presentación ausente no puede convertir un éxito en 502. El arreglo
 * `duplicates` no se interpreta (su forma no está publicada): los conteos publicados son la verdad.
 */
export function normalizeBundlePurchaseResult(input: unknown): BundlePurchaseResult | null {
  if (!isRecord(input)) return null;
  const purchaseId = toPositiveInteger(read(input, "purchaseId"));
  const snapshotHash = toSnapshotHash(read(input, "snapshotHash"));
  if (purchaseId === null || snapshotHash === null) return null;

  const games: BundlePurchaseResultGame[] = [];
  const rawGames = read(input, "games");
  if (Array.isArray(rawGames)) {
    for (const entry of rawGames) {
      const game = normalizeResultGame(entry);
      if (game === null) continue;
      games.push(game);
      if (games.length === MAX_PURCHASE_GAMES) break;
    }
  }

  return {
    purchaseId,
    created: read(input, "created") === true,
    alreadyReported: read(input, "alreadyReported") === true,
    source: toBoundedText(read(input, "source"), 32),
    bundleKey: toIdentifier(read(input, "bundleKey")),
    title: toBoundedText(read(input, "title"), MAX_TEXT_LENGTH),
    tierKey: toIdentifier(read(input, "tierKey")),
    platform: normalizeStore(read(input, "platform")),
    snapshotHash,
    selectedCount: toNonNegativeInteger(read(input, "selectedCount")) ?? 0,
    importedCount: toNonNegativeInteger(read(input, "importedCount")) ?? 0,
    duplicateCount: toNonNegativeInteger(read(input, "duplicateCount")) ?? 0,
    wishlistRemovedCount: toNonNegativeInteger(read(input, "wishlistRemovedCount")) ?? 0,
    games
  };
}

// --- historial de compras ------------------------------------------------

export type BundlePurchaseRecord = {
  readonly purchaseId: number;
  readonly source: string;
  readonly bundleKey: string;
  readonly title: string;
  readonly platform: string;
  /** `DateOnly?` en el backend: una compra puede no tener fecha declarada. */
  readonly acquiredAt: string | null;
  readonly importedCount: number;
  readonly duplicateCount: number;
  /** Cuántos juegos trae la compra; la fila se pinta con los conteos, no con una tabla aparte. */
  readonly gamesCount: number;
};

function normalizePurchaseRecord(value: unknown): BundlePurchaseRecord | null {
  if (!isRecord(value)) return null;
  const purchaseId = toPositiveInteger(read(value, "purchaseId"));
  const source = toBoundedText(read(value, "source"), 32);
  const bundleKey = toIdentifier(read(value, "bundleKey"));
  const title = toBoundedText(read(value, "title"), MAX_TEXT_LENGTH);
  const platform = normalizeStore(read(value, "platform"));
  const rawAcquiredAt = read(value, "acquiredAt");
  const acquiredAt = toIsoCalendarDate(rawAcquiredAt);
  if (purchaseId === null || source === null || bundleKey === null || title === null) return null;
  if (platform === null) return null;
  if (rawAcquiredAt !== null && rawAcquiredAt !== undefined && acquiredAt === null) return null;

  const rawGames = read(value, "games");
  return {
    purchaseId,
    source,
    bundleKey,
    title,
    platform,
    acquiredAt,
    importedCount: toNonNegativeInteger(read(value, "importedCount")) ?? 0,
    duplicateCount: toNonNegativeInteger(read(value, "duplicateCount")) ?? 0,
    gamesCount: Array.isArray(rawGames) ? rawGames.length : 0
  };
}

export type BundlePurchaseList = {
  readonly items: readonly BundlePurchaseRecord[];
  readonly count: number;
};

/**
 * Historial de compras del usuario. A diferencia de una búsqueda, una fila inválida invalida la lista
 * entera: esconder una compra en silencio haría creer que no existe, y el contrato de estos campos sí
 * está publicado. `count` cuenta las filas pintables (la respuesta no tiene paginación).
 */
export function normalizeBundlePurchaseList(input: unknown): BundlePurchaseList | null {
  if (!isRecord(input)) return null;
  const rawItems = read(input, "items");
  if (!Array.isArray(rawItems) || rawItems.length > MAX_BUNDLE_PURCHASES) return null;

  const items: BundlePurchaseRecord[] = [];
  for (const entry of rawItems) {
    const record = normalizePurchaseRecord(entry);
    if (record === null) return null;
    items.push(record);
  }
  return { items, count: items.length };
}

// --- keys duplicadas -----------------------------------------------------

export type BundlePurchaseDuplicate = {
  readonly purchaseId: number;
  readonly source: string;
  readonly bundleKey: string;
  readonly bundleTitle: string;
  /** `DateOnly?` en el backend. */
  readonly acquiredAt: string | null;
  readonly itemKey: string;
  readonly title: string;
  readonly gameId: number;
  readonly platform: string;
  /** `long?` en el DTO: un duplicado registrado antes de la fila de biblioteca puede no tenerla. */
  readonly userLibraryId: number | null;
  /** Título que ya tenía la ficha en la biblioteca; `null` cuando la biblioteca no lo publica. */
  readonly existingTitle: string | null;
};

function normalizeDuplicate(value: unknown): BundlePurchaseDuplicate | null {
  if (!isRecord(value)) return null;
  const purchaseId = toPositiveInteger(read(value, "purchaseId"));
  const source = toBoundedText(read(value, "source"), 32);
  const bundleKey = toIdentifier(read(value, "bundleKey"));
  const bundleTitle = toBoundedText(read(value, "bundleTitle"), MAX_TEXT_LENGTH);
  const rawAcquiredAt = read(value, "acquiredAt");
  const acquiredAt = toIsoCalendarDate(rawAcquiredAt);
  const itemKey = toIdentifier(read(value, "itemKey"));
  const title = toBoundedText(read(value, "title"), MAX_TEXT_LENGTH);
  const gameId = toPositiveInteger(read(value, "gameId"));
  const platform = normalizeStore(read(value, "platform"));
  const userLibraryId = toPositiveInteger(read(value, "userLibraryId"));
  const existingTitle = toBoundedText(read(value, "existingTitle"), MAX_TEXT_LENGTH);
  if (
    purchaseId === null ||
    source === null ||
    bundleKey === null ||
    bundleTitle === null ||
    itemKey === null ||
    title === null ||
    gameId === null ||
    platform === null
  ) {
    return null;
  }
  // `DateOnly?` en el DTO: null es ausente, pero una fecha que llega corrupta sí invalida la fila.
  if (rawAcquiredAt !== null && rawAcquiredAt !== undefined && acquiredAt === null) return null;
  return {
    purchaseId,
    source,
    bundleKey,
    bundleTitle,
    acquiredAt,
    itemKey,
    title,
    gameId,
    platform,
    userLibraryId,
    existingTitle
  };
}

export type BundlePurchaseDuplicateList = {
  readonly items: readonly BundlePurchaseDuplicate[];
  readonly count: number;
};

/**
 * Juegos duplicados: cada fila es un juego que ya estaba en la biblioteca en la misma plataforma. No hay
 * códigos de activación en el contrato y este cliente no los pide ni los muestra. Igual que el historial,
 * una fila inválida invalida la lista en vez de ocultar un duplicado.
 */
export function normalizeBundlePurchaseDuplicates(input: unknown): BundlePurchaseDuplicateList | null {
  if (!isRecord(input)) return null;
  const rawItems = read(input, "items");
  if (!Array.isArray(rawItems) || rawItems.length > MAX_PURCHASE_DUPLICATES) return null;

  const items: BundlePurchaseDuplicate[] = [];
  for (const entry of rawItems) {
    const duplicate = normalizeDuplicate(entry);
    if (duplicate === null) return null;
    items.push(duplicate);
  }
  return { items, count: items.length };
}

// --- candidatos del catálogo --------------------------------------------

export type CatalogCandidate = {
  readonly gameId: number;
  readonly title: string;
  readonly releaseYear: number | null;
  readonly imageUrl: string | null;
  readonly inLibrary: boolean;
};

export type CatalogCandidateList = {
  readonly items: readonly CatalogCandidate[];
  readonly count: number;
};

function normalizeCandidate(value: unknown): CatalogCandidate | null {
  if (!isRecord(value)) return null;
  const gameId = toPositiveInteger(read(value, "gameId"));
  const title = toBoundedText(read(value, "title"), MAX_TEXT_LENGTH);
  if (gameId === null || title === null) return null;

  const year = toPositiveInteger(read(value, "releaseYear"));
  return {
    gameId,
    title,
    releaseYear: year !== null && year >= 1000 && year <= 9999 ? year : null,
    imageUrl: toHttpsUrl(read(value, "imageUrl")),
    inLibrary: read(value, "inLibrary") === true
  };
}

/** Candidatos de ficha canónica. Es una lista de sugerencias: una fila rara se descarta sin invalidar la búsqueda. */
export function normalizeCatalogCandidates(input: unknown): CatalogCandidateList {
  const record = isRecord(input) ? input : {};
  const rawItems = read(record, "items");
  const items: CatalogCandidate[] = [];
  if (Array.isArray(rawItems)) {
    for (const entry of rawItems) {
      const candidate = normalizeCandidate(entry);
      if (candidate === null) continue;
      items.push(candidate);
      if (items.length === MAX_PURCHASE_CANDIDATES) break;
    }
  }
  return { items, count: items.length };
}

/** Texto de búsqueda reenviado por el BFF: recortado y dentro de los límites del contrato. */
export function parseCatalogCandidateQuery(value: string | null): string | null {
  if (typeof value !== "string") return null;
  const text = value.trim();
  return text.length >= CANDIDATE_QUERY_MIN && text.length <= CANDIDATE_QUERY_MAX ? text : null;
}

/** Límite de candidatos: entero 1..10; cualquier otra cosa es `null` y el BFF aplica su default. */
export function parseCatalogCandidateLimit(value: string | null): number | null {
  if (value === null || !/^[1-9]\d*$/.test(value)) return null;
  const parsed = Number(value);
  return parsed >= 1 && parsed <= MAX_PURCHASE_CANDIDATES ? parsed : null;
}
