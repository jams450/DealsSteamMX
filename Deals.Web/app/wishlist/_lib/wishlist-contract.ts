type UnknownRecord = Record<string, unknown>;

// Estados de la wishlist. `inaccessible` cubre la trampa real de la API de Steam: una wishlist privada
// devuelve `{"response":{}}`, indistinguible de "sin juegos", así que la UI nunca lo muestra como 0.
export const WISHLIST_STATES = ["ok", "inaccessible", "no_steam_id", "never_synced"] as const;
export type WishlistState = (typeof WISHLIST_STATES)[number];

export type WishlistItem = {
  readonly appId: number;
  readonly name: string;
  readonly imageUrl: string | null;
  readonly priority: number | null;
  readonly addedAt: string | null;
  readonly itadGameId: string | null;
  readonly refreshedAt: string | null;
  // Snapshot de precios del juego. Nullable por diseño: un juego recién importado todavía no tiene
  // observación, y cada campo se pinta como "—" sin inventar un 0.
  readonly basePriceMinor: number | null;
  readonly baseCurrency: string | null;
  readonly historyLowMinor: number | null;
  readonly historyLowCurrency: string | null;
  readonly bestOfficialMinor: number | null;
  readonly bestKeyshopMinor: number | null;
};

export type WishlistResponse = {
  readonly state: WishlistState;
  readonly syncedAt: string | null;
  readonly items: readonly WishlistItem[];
  // Umbral del score de la wishlist. El backend lo manda; si todavía no lo manda (o llega inválido) se
  // usa el default y la página sigue funcionando.
  readonly minViableDiscountPercent: number;
};

export type WishlistPreferences = {
  readonly minViableDiscountPercent: number;
};

export const MIN_VIABLE_DISCOUNT_PERCENT_MIN = 0;
export const MIN_VIABLE_DISCOUNT_PERCENT_MAX = 95;
export const MIN_VIABLE_DISCOUNT_PERCENT_DEFAULT = 50;

export type WishlistSyncResponse = {
  readonly state: WishlistState;
  readonly itemCount: number;
  readonly added: number;
  readonly updated: number;
  readonly removed: number;
  readonly refreshed: number;
  readonly failed: number;
  readonly syncedAt: string | null;
  // El sync también trae de Steam los juegos que no tenían ficha local: una llamada por juego faltante.
  // `refreshed`/`failed` siguen en 0 a propósito: el refresco de precios es del job y del botón por fila.
  readonly fetchedFromSteam: number;
  readonly fetchFailed: number;
};

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

function read(value: UnknownRecord, key: string): unknown {
  return value[key] ?? value[key.charAt(0).toUpperCase() + key.slice(1)];
}

function toPositiveInteger(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

// Prioridad de Steam: rango entero 0-based (0 = el primero de la lista). Negativos o decimales no valen.
function toNonNegativeInteger(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

// Umbral del score: entero acotado 0..95. Se valida aparte del default para poder distinguir
// "ausente" (la respuesta del GET lo tolera) de "inválido" (la respuesta del PUT no).
function toDiscountThreshold(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= MIN_VIABLE_DISCOUNT_PERCENT_MIN && parsed <= MIN_VIABLE_DISCOUNT_PERCENT_MAX
    ? parsed
    : null;
}

// Importes en la unidad mínima de su moneda (mismo criterio que `lib/contracts/steam.ts`): entero
// seguro y no negativo. Cualquier otra cosa se descarta a null, nunca se recorta a un número plausible.
function toPriceMinor(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

// ISO-4217 de tres letras. Es la única forma que llega a `Intl.NumberFormat`: un código malformado
// lanzaría RangeError en el render.
function toCurrencyCode(value: unknown): string | null {
  const text = toText(value)?.toUpperCase();
  return text !== undefined && /^[A-Z]{3}$/.test(text) ? text : null;
}

function toText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

// Igual que `toText`, pero acotado: los textos vienen del proveedor y no deben inflar la respuesta.
function toBoundedText(value: unknown, maxLength: number): string | null {
  const text = toText(value);
  return text === null ? null : text.slice(0, maxLength);
}

// Solo ISO-8601 con hora y zona: cualquier otra cosa se descarta (null) en vez de llegar al render.
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

function toIsoDateTime(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !ISO_DATE_TIME.test(text)) return null;
  // El regex no valida el calendario: "2026-02-30" se convierte en marzo, así que se revisa el día.
  const day = text.slice(0, 10);
  const date = new Date(`${day}T00:00:00Z`);
  if (Number.isNaN(date.getTime()) || date.toISOString().slice(0, 10) !== day) return null;
  return Number.isNaN(new Date(text).getTime()) ? null : text;
}

// Imagen de portada: solo https absoluto. Cualquier otra cosa se descarta antes de llegar a un `src`.
function toHttpsUrl(value: unknown): string | null {
  const text = toText(value);
  if (text === null) return null;
  try {
    return new URL(text).protocol === "https:" ? text : null;
  } catch {
    return null;
  }
}

// Toda estado nuevo debe añadirse aquí **y** al array de arriba: `normalizeWishlistResponse` devuelve
// null cuando no valida, así que un estado sin soporte se convierte en error de upstream, no en copy
// inventado. El backend construye el contrato; el cliente no adivina.
function toWishlistState(value: unknown): WishlistState | null {
  const text = toText(value)?.toLowerCase();
  return text === "ok" || text === "inaccessible" || text === "no_steam_id" || text === "never_synced"
    ? text
    : null;
}

// Los contadores del reporte son parte del contrato: si falta uno, el reporte entero se rechaza en vez
// de mostrar un 0 inventado.
function toCount(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

const MAX_ITEM_NAME_LENGTH = 256;
const MAX_ITAD_GAME_ID_LENGTH = 64;
// Tope defensivo del payload: una wishlist real cabe de sobra; un payload abusivo no debe convertirse
// en miles de filas en el render. ponytail: trunca en silencio, subir el tope si alguna cuenta lo pasa.
const MAX_WISHLIST_ITEMS = 5000;

function normalizeWishlistItem(value: unknown): WishlistItem | null {
  if (!isRecord(value)) return null;

  const appId = toPositiveInteger(read(value, "appId"));
  const name = toBoundedText(read(value, "name"), MAX_ITEM_NAME_LENGTH);
  if (appId === null || name === null) return null;

  return {
    appId,
    name,
    imageUrl: toHttpsUrl(read(value, "imageUrl")),
    priority: toNonNegativeInteger(read(value, "priority")),
    addedAt: toIsoDateTime(read(value, "addedAt")),
    itadGameId: toBoundedText(read(value, "itadGameId"), MAX_ITAD_GAME_ID_LENGTH),
    refreshedAt: toIsoDateTime(read(value, "refreshedAt")),
    basePriceMinor: toPriceMinor(read(value, "basePriceMinor")),
    baseCurrency: toCurrencyCode(read(value, "baseCurrency")),
    historyLowMinor: toPriceMinor(read(value, "historyLowMinor")),
    historyLowCurrency: toCurrencyCode(read(value, "historyLowCurrency")),
    // Los mínimos de tiendas llegan ya convertidos a MXN, así que no traen moneda propia.
    bestOfficialMinor: toPriceMinor(read(value, "bestOfficialMinor")),
    bestKeyshopMinor: toPriceMinor(read(value, "bestKeyshopMinor"))
  };
}

export function normalizeWishlistResponse(input: unknown): WishlistResponse | null {
  if (!isRecord(input)) return null;

  const state = toWishlistState(read(input, "state"));
  if (state === null) return null;

  const rawItems = read(input, "items");
  const items: WishlistItem[] = [];
  if (Array.isArray(rawItems)) {
    for (const entry of rawItems) {
      const item = normalizeWishlistItem(entry);
      if (item === null) continue;
      items.push(item);
      if (items.length === MAX_WISHLIST_ITEMS) break;
    }
  }

  return {
    state,
    syncedAt: toIsoDateTime(read(input, "syncedAt")),
    // El umbral es resiliente a propósito: mientras el backend no lo mande, la página queda en 50.
    minViableDiscountPercent:
      toDiscountThreshold(read(input, "minViableDiscountPercent")) ?? MIN_VIABLE_DISCOUNT_PERCENT_DEFAULT,
    items
  };
}

// Respuesta del PUT de preferencias: aquí no se inventa nada. Si el campo no llega como entero 0..95,
// la respuesta entera se rechaza en vez de devolver un umbral que el usuario no eligió.
export function normalizeWishlistPreferencesResponse(input: unknown): WishlistPreferences | null {
  if (!isRecord(input)) return null;

  const threshold = toDiscountThreshold(read(input, "minViableDiscountPercent"));
  return threshold === null ? null : { minViableDiscountPercent: threshold };
}

export function normalizeWishlistSyncResponse(input: unknown): WishlistSyncResponse | null {
  if (!isRecord(input)) return null;

  const state = toWishlistState(read(input, "state"));
  if (state === null) return null;

  const itemCount = toCount(read(input, "itemCount"));
  const added = toCount(read(input, "added"));
  const updated = toCount(read(input, "updated"));
  const removed = toCount(read(input, "removed"));
  const refreshed = toCount(read(input, "refreshed"));
  const failed = toCount(read(input, "failed"));
  const fetchedFromSteam = toCount(read(input, "fetchedFromSteam"));
  const fetchFailed = toCount(read(input, "fetchFailed"));
  if (
    itemCount === null ||
    added === null ||
    updated === null ||
    removed === null ||
    refreshed === null ||
    failed === null ||
    fetchedFromSteam === null ||
    fetchFailed === null
  ) {
    return null;
  }

  return {
    state,
    itemCount,
    added,
    updated,
    removed,
    refreshed,
    failed,
    syncedAt: toIsoDateTime(read(input, "syncedAt")),
    fetchedFromSteam,
    fetchFailed
  };
}
