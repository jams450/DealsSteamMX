// Rutas relativas a propósito: este módulo se cubre con `node --test`, que no resuelve el alias `@/`.
import { normalizeReview, playStatusOf, type Review, type ReviewStatus } from "../../../lib/contracts/reviews.ts";
import { toStoreKey } from "../../../lib/contracts/stores.ts";

type UnknownRecord = Record<string, unknown>;

// Estados de `user_library.state` que la biblioteca sabe pintar. `wished` comparte tabla con la
// wishlist: se muestra con su propio tag y nunca como posesión. Un estado que no esté aquí se
// descarta (la fila se cae), nunca se mapea a `owned`: una suscripción no puede leerse como compra.
export const LIBRARY_STATES = ["owned", "subscription", "wished"] as const;
export type LibraryState = (typeof LIBRARY_STATES)[number];

// Tope del archivo de Playnite en bytes: se valida en el cliente (aviso temprano) y en el BFF (el
// límite real). El export real pesa menos de 1 MiB, así que 10 MiB es holgado.
export const LIBRARY_IMPORT_MAX_BYTES = 10 * 1024 * 1024;

// Resultado del binding de precios que calcula el backend. `none` es el valor seguro: cualquier cosa que
// no esté en esta lista (o que falte) cae ahí, nunca a `exact`, porque un vínculo inventado mostraría un
// precio que nadie confirmó.
export const LIBRARY_PRICE_STATES = ["exact", "title_candidate", "none", "subscription"] as const;
export type LibraryPriceState = (typeof LIBRARY_PRICE_STATES)[number];

// Por qué identidad se llegó al precio: appid de Steam, UUID de ITAD o coincidencia de título.
export const LIBRARY_BINDING_SOURCES = ["steam", "itad", "title"] as const;
export type LibraryBindingSource = (typeof LIBRARY_BINDING_SOURCES)[number];

export type LibraryItem = {
  readonly userLibraryId: number;
  readonly store: string;
  // Identidad de tienda: solo se usa como dato de la fila (nunca se pinta en la UI).
  readonly storeGameId: string;
  readonly title: string;
  // Portada del juego. A diferencia del precio, una suscripción sí puede tener imagen: el arte no
  // afirma posesión, así que no se anula por `state`.
  readonly imageUrl: string | null;
  readonly state: LibraryState;
  readonly isInstalled: boolean;
  readonly addedAt: string | null;
  readonly importedAt: string | null;
  readonly priceState: LibraryPriceState;
  // Identidad del vínculo: se normaliza para no pintarla nunca; la UI solo distingue por `priceState`.
  readonly bindingSource: LibraryBindingSource | null;
  readonly steamAppId: number | null;
  // Identidad canónica del juego: es la llave para reseñar. `null` significa que el catálogo todavía no
  // lo reconoce, así que la fila no se puede reseñar (no se ofrece una acción rota).
  readonly gameId: number | null;
  // La reseña más reciente del usuario para este `(gameId, platform)`, si existe. Un juego puede tener
  // varias reseñas en la misma plataforma (una por partida); la fila trae la última y el drawer la lista
  // completa. Se pinta solo cuando coincide con la fila; ver `normalizeLibraryItem`.
  readonly review: Review | null;
  // Los dos mínimos ya vienen convertidos a MXN por el backend; el precio base y el mínimo histórico
  // llegan en la moneda del proveedor (`baseCurrency`) y no se convierten en el navegador.
  readonly bestOfficialMinor: number | null;
  readonly bestKeyshopMinor: number | null;
  readonly historyLowMinor: number | null;
  readonly basePriceMinor: number | null;
  readonly baseCurrency: string | null;
  // Marca de favorito del juego canónico. `false` en una fila sin identidad: un favorito cuelga de `games`,
  // no de la fila de import.
  readonly isFavorite: boolean;
  // Años en que se jugó ese par, del más nuevo al más viejo, derivados de TODAS sus reseñas. Un juego
  // rejugado en 2026 y en 2030 aparece en los dos años; un arreglo vacío es "sin fecha".
  readonly playedYears: readonly number[];
};

export type LibraryResponse = {
  readonly items: readonly LibraryItem[];
};

// Una plataforma dentro de un juego agrupado: los datos de la fila que sí cambian por tienda.
export type LibraryPlatform = {
  readonly store: string;
  readonly storeGameId: string;
  readonly userLibraryId: number;
  readonly state: LibraryState;
  readonly isInstalled: boolean;
  readonly addedAt: string | null;
  readonly review: Review | null;
};

// Un juego de la biblioteca con todas sus plataformas fusionadas. `key` es la identidad de la fila
// pintada: `game:<id>` cuando el catálogo lo reconoce, `row:<userLibraryId>` cuando no.
export type LibraryGame = {
  readonly key: string;
  readonly gameId: number | null;
  readonly title: string;
  readonly imageUrl: string | null;
  readonly platforms: readonly LibraryPlatform[];
  readonly stores: readonly string[];
  readonly states: readonly LibraryState[];
  readonly isInstalled: boolean;
  readonly hasReview: boolean;
  readonly lastReview: Review | null;
  // Estado de juego del grupo: el de su reseña representativa, o `backlog` cuando no hay ninguna. Es lo
  // que filtra el toolbar, y `backlog` significa exactamente "por jugar".
  readonly playStatus: ReviewStatus | "backlog";
  // Unión de los años jugados de todas sus plataformas, del más nuevo al más viejo.
  readonly playedYears: readonly number[];
  readonly isFavorite: boolean;
  readonly item: LibraryItem;
};

// Marca temporal de una reseña, para elegir la más reciente. `updated` manda; si falta, `created`. Una
// fecha ilegible devuelve `null` (más antigua que cualquiera con fecha válida).
function reviewTimestamp(review: Review): number | null {
  const parsed = Date.parse(review.updated ?? review.created ?? "");
  return Number.isFinite(parsed) ? parsed : null;
}

// Unión de los años de todas las plataformas del grupo, del más nuevo al más viejo y sin repetidos. Los
// años llegan del backend ya derivados de cada reseña (mes de fin, o de inicio si no hay fin).
function mergePlayedYears(values: readonly (readonly number[])[]): number[] {
  const years = new Set<number>();
  for (const list of values) {
    for (const year of list) {
      if (Number.isSafeInteger(year) && year > 0) years.add(year);
    }
  }
  return [...years].sort((left, right) => right - left);
}

/**
 * Agrupa las filas de la biblioteca por juego. Las filas con `gameId` se fusionan por ese id; una fila
 * sin identidad canónica es su propio grupo (nunca se fusiona por título: el título no es una llave).
 * Función pura: no muta la entrada ni lee el reloj.
 */
export function groupLibraryItems(items: readonly LibraryItem[]): LibraryGame[] {
  const groups = new Map<string, LibraryItem[]>();

  for (const item of items) {
    const key = item.gameId === null ? `row:${item.userLibraryId}` : `game:${item.gameId}`;
    const bucket = groups.get(key);
    if (bucket === undefined) groups.set(key, [item]);
    else bucket.push(item);
  }

  const result: LibraryGame[] = [];
  for (const [key, rows] of groups) {
    const first = rows[0];
    if (first === undefined) continue;

    const stores: string[] = [];
    const states: LibraryState[] = [];
    const platforms: LibraryPlatform[] = [];
    let imageUrl: string | null = null;
    let isInstalled = false;
    let isFavorite = false;
    let lastReview: Review | null = null;
    let lastMark: number | null = null;
    const playedYears: (readonly number[])[] = [];

    for (const row of rows) {
      if (!stores.includes(row.store)) stores.push(row.store);
      if (!states.includes(row.state)) states.push(row.state);
      if (imageUrl === null && row.imageUrl !== null) imageUrl = row.imageUrl;
      if (row.isInstalled) isInstalled = true;
      // El favorito es del juego: si cualquier fila del grupo lo trae marcado, el grupo lo está.
      if (row.isFavorite) isFavorite = true;
      playedYears.push(row.playedYears);

      if (row.review !== null) {
        const mark = reviewTimestamp(row.review);
        // Un empate conserva la primera en orden de entrada; solo una marca estrictamente mayor gana.
        const better =
          lastReview === null ||
          (mark !== null && (lastMark === null || mark > lastMark));
        if (better) {
          lastReview = row.review;
          lastMark = mark;
        }
      }

      platforms.push({
        store: row.store,
        storeGameId: row.storeGameId,
        userLibraryId: row.userLibraryId,
        state: row.state,
        isInstalled: row.isInstalled,
        addedAt: row.addedAt,
        review: row.review
      });
    }

    result.push({
      key,
      gameId: first.gameId,
      title: first.title,
      imageUrl,
      platforms,
      stores,
      states,
      isInstalled,
      hasReview: lastReview !== null,
      lastReview,
      // Una reseña presente pero no la más reciente posible no importa: el estado se lee de la
      // representativa, así que `completed` gana sobre un `dropped` anterior y viceversa.
      playStatus: playStatusOf(lastReview),
      playedYears: mergePlayedYears(playedYears),
      isFavorite,
      item: first
    });
  }

  return result.sort(
    (left, right) => left.title.localeCompare(right.title, "es-MX") || left.key.localeCompare(right.key)
  );
}

export type LibraryStoreCount = {
  readonly store: string;
  readonly count: number;
};

export type LibraryImportReport = {
  readonly imported: number;
  readonly updated: number;
  readonly unresolved: number;
  readonly unsupportedSource: number;
  readonly byStore: readonly LibraryStoreCount[];
};

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

// El backend responde camelCase; se tolera PascalCase porque los DTOs de .NET pueden reconfigurarse.
function read(value: UnknownRecord, key: string): unknown {
  return value[key] ?? value[key.charAt(0).toUpperCase() + key.slice(1)];
}

function toText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function toBoundedText(value: unknown, maxLength: number): string | null {
  const text = toText(value);
  return text === null ? null : text.slice(0, maxLength);
}

function toPositiveInteger(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

function toNonNegativeInteger(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

// Timestamp normalizado a UTC. Acepta día suelto, ISO sin zona (se asume UTC: es lo que significa un
// DateTime sin `Kind` guardado así) y zona explícita; la fracción de segundos se descarta. El
// renderizador formatea siempre con `timeZone: "UTC"`, así que un `Added` de Playnite no cambia de día.
const UTC_TIMESTAMP = /^(\d{4}-\d{2}-\d{2})(T\d{2}:\d{2}(?::\d{2})?)?(?:\.\d+)?(Z|[+-]\d{2}:\d{2})?$/;

function toUtcTimestamp(value: unknown): string | null {
  const text = toText(value);
  if (text === null) return null;

  const match = UTC_TIMESTAMP.exec(text);
  if (match === null) return null;

  const day = match[1] ?? "";
  const calendar = new Date(`${day}T00:00:00Z`);
  if (Number.isNaN(calendar.getTime()) || calendar.toISOString().slice(0, 10) !== day) return null;

  const normalized = `${day}${match[2] ?? "T00:00:00"}${match[3] ?? "Z"}`;
  return Number.isNaN(new Date(normalized).getTime()) ? null : normalized;
}

// Tienda normalizada a minúsculas: el filtro y el conteo agrupan por este valor, así que "GOG" y "gog"
// no pueden aparecer como dos tiendas distintas. El nombre visible lo pone la capa de UI.
function toStore(value: unknown): string | null {
  const text = toBoundedText(value, 64)?.toLowerCase();
  return text === undefined ? null : text;
}

function toLibraryState(value: unknown): LibraryState | null {
  const text = toText(value)?.toLowerCase();
  return text === "owned" || text === "subscription" || text === "wished" ? text : null;
}

function toPriceState(value: unknown): LibraryPriceState {
  const text = toText(value)?.toLowerCase();
  return text === "exact" || text === "title_candidate" || text === "none" || text === "subscription" ? text : "none";
}

function toBindingSource(value: unknown): LibraryBindingSource | null {
  const text = toText(value)?.toLowerCase();
  return text === "steam" || text === "itad" || text === "title" ? text : null;
}

// Importes en la unidad mínima de su moneda (mismo criterio que la wishlist): entero seguro y no
// negativo. Un decimal, un negativo o un texto raro se descartan a null, nunca se redondean.
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

const MAX_TITLE_LENGTH = 256;
const MAX_STORE_GAME_ID_LENGTH = 128;
// Tope defensivo del render: el export real trae ~2.6k filas. ponytail: trunca en silencio; subirlo si
// alguna cuenta supera el tope.
const MAX_LIBRARY_ITEMS = 20_000;
const MAX_BY_STORE_ENTRIES = 64;

// Años jugados: solo enteros de 4 dígitos, deduplicados y ordenados del más nuevo al más viejo. Un valor
// raro se descarta en vez de pintar "año NaN".
const MAX_PLAYED_YEARS = 128;

function normalizePlayedYears(value: unknown): readonly number[] {
  if (!Array.isArray(value)) return [];

  const years = new Set<number>();
  for (const entry of value) {
    if (typeof entry !== "number" || !Number.isSafeInteger(entry) || entry < 1000 || entry > 9999) continue;
    years.add(entry);
    if (years.size === MAX_PLAYED_YEARS) break;
  }
  return [...years].sort((left, right) => right - left);
}

function normalizeLibraryItem(value: unknown): LibraryItem | null {
  if (!isRecord(value)) return null;

  const userLibraryId = toPositiveInteger(read(value, "userLibraryId"));
  const store = toStore(read(value, "store"));
  const storeGameId = toBoundedText(read(value, "storeGameId"), MAX_STORE_GAME_ID_LENGTH);
  const title = toBoundedText(read(value, "title"), MAX_TITLE_LENGTH);
  const state = toLibraryState(read(value, "state"));
  if (userLibraryId === null || store === null || storeGameId === null || title === null || state === null) {
    return null;
  }

  // Una suscripción no puede llevar precio aunque el backend lo mande: el estado se fuerza aquí, así que
  // ningún render puede mostrar un importe ni un vínculo de Game Pass.
  const subscription = state === "subscription";

  // La reseña solo se acepta si pertenece a esta fila: un `gameId`/`platform` que no coincide se descarta
  // en vez de pintar una reseña ajena sobre el juego equivocado.
  const gameId = toPositiveInteger(read(value, "gameId"));
  const platform = toStoreKey(store);
  const review = normalizeReview(read(value, "review"));
  const matchedReview =
    review !== null && gameId !== null && platform !== null && review.gameId === gameId && review.platform === platform
      ? review
      : null;

  return {
    userLibraryId,
    store,
    storeGameId,
    title,
    imageUrl: toHttpsUrl(read(value, "imageUrl")),
    // Solo un `true` literal marca favorito: un dato dudoso nunca pinta la estrella.
    isFavorite: read(value, "isFavorite") === true,
    playedYears: normalizePlayedYears(read(value, "playedYears")),
    state,
    // Solo un `true` literal marca instalado: cualquier otra cosa se queda en `false`, así que el
    // indicador nunca puede aparecer por un dato dudoso.
    isInstalled: read(value, "isInstalled") === true,
    addedAt: toUtcTimestamp(read(value, "addedAt")),
    importedAt: toUtcTimestamp(read(value, "importedAt")),
    priceState: subscription ? "subscription" : toPriceState(read(value, "priceState")),
    bindingSource: toBindingSource(read(value, "bindingSource")),
    steamAppId: toPositiveInteger(read(value, "steamAppId")),
    gameId,
    review: matchedReview,
    bestOfficialMinor: subscription ? null : toPriceMinor(read(value, "bestOfficialMinor")),
    bestKeyshopMinor: subscription ? null : toPriceMinor(read(value, "bestKeyshopMinor")),
    historyLowMinor: subscription ? null : toPriceMinor(read(value, "historyLowMinor")),
    basePriceMinor: subscription ? null : toPriceMinor(read(value, "basePriceMinor")),
    baseCurrency: subscription ? null : toCurrencyCode(read(value, "baseCurrency"))
  };
}

// Acepta `{ items: [...] }` (contrato) y también el arreglo crudo, porque el BFF es el único que habla
// con la API y conviene que un cambio de envoltura no rompa la página. Un `items` que no es arreglo sí
// se rechaza: una forma inválida no puede leerse como "biblioteca vacía".
export function normalizeLibraryResponse(input: unknown): LibraryResponse | null {
  const rawItems = Array.isArray(input)
    ? input
    : isRecord(input) && Array.isArray(read(input, "items"))
      ? (read(input, "items") as unknown[])
      : null;
  if (rawItems === null) return null;

  const items: LibraryItem[] = [];
  for (const entry of rawItems) {
    const item = normalizeLibraryItem(entry);
    if (item === null) continue;
    items.push(item);
    if (items.length === MAX_LIBRARY_ITEMS) break;
  }

  return { items };
}

function normalizeByStore(value: unknown): LibraryStoreCount[] {
  if (!isRecord(value)) return [];

  const counts: LibraryStoreCount[] = [];
  for (const [key, rawCount] of Object.entries(value)) {
    const store = toStore(key);
    const count = toNonNegativeInteger(rawCount);
    if (store === null || count === null) continue;
    counts.push({ store, count });
    if (counts.length === MAX_BY_STORE_ENTRIES) break;
  }

  return counts.sort((left, right) => right.count - left.count || left.store.localeCompare(right.store, "es-MX"));
}

// Los cuatro contadores son parte del contrato: si falta uno, el reporte entero se rechaza en vez de
// mostrar un 0 inventado. `byStore` es el desglose: si llega raro se pierde el desglose, no el reporte.
export function normalizeLibraryImportResponse(input: unknown): LibraryImportReport | null {
  if (!isRecord(input)) return null;

  const imported = toNonNegativeInteger(read(input, "imported"));
  const updated = toNonNegativeInteger(read(input, "updated"));
  const unresolved = toNonNegativeInteger(read(input, "unresolved"));
  const unsupportedSource = toNonNegativeInteger(read(input, "unsupportedSource"));
  if (imported === null || updated === null || unresolved === null || unsupportedSource === null) return null;

  return {
    imported,
    updated,
    unresolved,
    unsupportedSource,
    byStore: normalizeByStore(read(input, "byStore"))
  };
}
