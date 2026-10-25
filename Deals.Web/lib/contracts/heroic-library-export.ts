// Contrato del export de biblioteca de Heroic (odd/tasks/heroic-library-export.md, L4), la alternativa
// manual a Playnite. Es un sobre versionado, no un arreglo de filas:
//
//   { format: "dealext-library", version: 1, source: "heroic", exportedAt: ISO UTC,
//     games: [{ store: "epic"|"gog", storeGameId, title, isInstalled }] }
//
// Reglas que este módulo hace cumplir (las mismas que el backend, que vuelve a validar todo):
//   · el sobre es de Heroic; una fila de Playnite no lo es y viceversa,
//   · solo epic y gog — Steam sigue entrando por el perfil público, no por un archivo,
//   · la lista blanca es el contrato: rutas, tokens y arte se descartan, nunca se reenvían,
//   · isInstalled es obligatorio y booleano: ausente no puede leerse como «no instalado»,
//   · el id de GOG es numérico y el de Epic es opaco (el `app_name` exacto que usa Playnite),
//   · una fila mala invalida el archivo completo: el backend escribe en una sola transacción, así que
//     reenviar un archivo parcial no es «importar lo bueno», es perder lo bueno.
//
// La posesión (`owned`) y la fecha de alta no viajan en el sobre: el servidor escribe `owned` para cada fila
// porque Heroic solo lista entradas de una biblioteca propia. Es la afirmación del usuario — la misma que
// lleva una importación de Playnite —, no una prueba de posesión.
//
// Este módulo no importa nada, a propósito: `node --test` lo cubre y no resuelve el alias `@/`.

export const HEROIC_LIBRARY_EXPORT_FORMAT = "dealext-library";
export const HEROIC_LIBRARY_EXPORT_VERSION = 1;
export const HEROIC_LIBRARY_EXPORT_SOURCE = "heroic";

/** Mismo tope que la importación de Playnite: Heroic es otra puerta de entrada, no una más ancha. */
export const HEROIC_LIBRARY_EXPORT_MAX_BYTES = 10 * 1024 * 1024;
export const HEROIC_LIBRARY_EXPORT_MAX_GAMES = 10_000;

/** Espejo de `HeroicLibraryExportLimits` del backend (user_library.title / store_game_id). */
const MAX_TITLE_LENGTH = 256;
const MAX_STORE_GAME_ID_LENGTH = 64;

export const HEROIC_LIBRARY_EXPORT_STORES = ["epic", "gog"] as const;
export type HeroicLibraryStore = (typeof HEROIC_LIBRARY_EXPORT_STORES)[number];

export type HeroicLibraryGame = {
  readonly store: HeroicLibraryStore;
  readonly storeGameId: string;
  readonly title: string;
  readonly isInstalled: boolean;
};

export type HeroicLibraryExport = {
  readonly format: typeof HEROIC_LIBRARY_EXPORT_FORMAT;
  readonly version: typeof HEROIC_LIBRARY_EXPORT_VERSION;
  readonly source: typeof HEROIC_LIBRARY_EXPORT_SOURCE;
  /** Instante UTC normalizado a `toISOString()`. */
  readonly exportedAt: string;
  readonly games: readonly HeroicLibraryGame[];
};

type UnknownRecord = Record<string, unknown>;

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

// Los nombres son exactamente los del contrato (minúsculas, camelCase): el export lo produce nuestro
// script, así que aceptar otras formas solo escondería un archivo que el contrato no define.
function toText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function toBoundedText(value: unknown, maxLength: number): string | null {
  const text = toText(value);
  return text === null || text.length > maxLength ? null : text;
}

// El id de tienda es identidad, no texto de pantalla: nunca se recorta en silencio. Un id con espacios en los
// extremos se rechaza para no escribir una llave que el archivo no contenía, y un carácter de control se
// rechaza porque PostgreSQL no puede almacenarlo. Mismo criterio que `RequireStoreGameId` del backend.
const CONTROL_CHARACTER = /[\u0000-\u001F\u007F]/;

function toStoreGameId(value: unknown, maxLength: number): string | null {
  if (typeof value !== "string") return null;
  if (value.length === 0 || value.length > maxLength) return null;
  if (value !== value.trim()) return null;
  if (CONTROL_CHARACTER.test(value)) return null;
  return value;
}

function toStore(value: unknown): HeroicLibraryStore | null {
  const text = toText(value)?.toLowerCase();
  return text === "epic" || text === "gog" ? text : null;
}

// Solo dígitos ASCII: un id de GOG es un entero, y `Number` aceptaría "1e9" o "-1".
function isNumericId(value: string): boolean {
  return /^\d+$/.test(value);
}

// ISO 8601 con zona cero explícita. Se exige `Z` o `+00:00` porque el sobre promete UTC: un desfase local
// se rechaza en vez de convertirse, que es lo mismo que hace el backend.
const UTC_TIMESTAMP = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?(?:\.(\d{1,3}))?(Z|\+00:00)$/;

function toUtcTimestamp(value: unknown): string | null {
  const text = toText(value);
  if (text === null) return null;

  const match = UTC_TIMESTAMP.exec(text);
  if (match === null) return null;

  const [, year, month, day, hour, minute, second, fraction] = match;
  const utc = new Date(
    Date.UTC(
      Number(year),
      Number(month) - 1,
      Number(day),
      Number(hour),
      Number(minute),
      Number(second ?? "0"),
      Number((fraction ?? "0").padEnd(3, "0"))
    )
  );
  if (Number.isNaN(utc.getTime())) return null;

  // `Date.UTC` redondea un día imposible (2026-02-30 → marzo), así que la ida y vuelta es la comprobación
  // real del calendario.
  if (
    utc.getUTCFullYear() !== Number(year) ||
    utc.getUTCMonth() !== Number(month) - 1 ||
    utc.getUTCDate() !== Number(day) ||
    utc.getUTCHours() !== Number(hour) ||
    utc.getUTCMinutes() !== Number(minute) ||
    utc.getUTCSeconds() !== Number(second ?? "0")
  ) {
    return null;
  }

  return utc.toISOString();
}

function toGame(value: unknown): HeroicLibraryGame | null {
  if (!isRecord(value)) return null;

  const store = toStore(value.store);
  const storeGameId = toStoreGameId(value.storeGameId, MAX_STORE_GAME_ID_LENGTH);
  const title = toBoundedText(value.title, MAX_TITLE_LENGTH);
  // Solo un booleano literal: `"true"`, `1` o ausente se rechazan en vez de caer a `false`.
  const isInstalled = value.isInstalled;
  if (store === null || storeGameId === null || title === null || typeof isInstalled !== "boolean") return null;
  if (store === "gog" && !isNumericId(storeGameId)) return null;

  // Objeto nuevo: la lista blanca se aplica aquí, no en el render.
  return { store, storeGameId, title, isInstalled };
}

/**
 * Valida el archivo completo y devuelve el sobre normalizado, o `null` si algo no cumple el contrato. Es
 * estricto a propósito: el mismo validador corre en el navegador (aviso temprano) y en el BFF (la puerta
 * real), y un `null` se traduce en un 400 con mensaje, nunca en una importación parcial.
 *
 * Los duplicados se conservan: el backend se queda con el primero de cada `(tienda, id)` y contarlos aquí
 * de otra forma haría que el reporte mintiera.
 */
export function parseHeroicLibraryExport(input: unknown): HeroicLibraryExport | null {
  if (!isRecord(input)) return null;

  if (input.format !== HEROIC_LIBRARY_EXPORT_FORMAT) return null;
  if (input.version !== HEROIC_LIBRARY_EXPORT_VERSION) return null;
  if (input.source !== HEROIC_LIBRARY_EXPORT_SOURCE) return null;

  const exportedAt = toUtcTimestamp(input.exportedAt);
  if (exportedAt === null) return null;

  if (!Array.isArray(input.games) || input.games.length === 0) return null;
  if (input.games.length > HEROIC_LIBRARY_EXPORT_MAX_GAMES) return null;

  const games: HeroicLibraryGame[] = [];
  for (const entry of input.games) {
    const game = toGame(entry);
    // Una fila mala invalida el archivo: el backend aborta la transacción completa y no escribe nada.
    if (game === null) return null;
    games.push(game);
  }

  return {
    format: HEROIC_LIBRARY_EXPORT_FORMAT,
    version: HEROIC_LIBRARY_EXPORT_VERSION,
    source: HEROIC_LIBRARY_EXPORT_SOURCE,
    exportedAt,
    games
  };
}
