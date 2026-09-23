// Contrato del relleno de portadas de la biblioteca. El cliente manda un id de proveedor — el appid de
// Steam o el id de IGDB, exactamente uno de los dos —, nunca una URL: la portada la resuelve y la guarda
// el servidor (Steam o IGDB), igual que el resto de datos de proveedor. La elección es tan estricta como
// la del backend: los dos campos a la vez, ninguno, o un tercer campo (`imageUrl`, `title`) es inválido
// aquí y 400 allá, porque la forma es discriminada y no se adivina qué quiso el llamador.
// La portada es decorativa: ninguna operación de este contrato reclama identidad ni toca precios.
//
// Rutas relativas a propósito: este módulo se cubre con `node --test`, que no resuelve el alias `@/`.
import { toStoreKey } from "./stores.ts";

/** Tope de la pasada, el mismo número que valida el backend. */
export const COVER_SYNC_MAX_LIMIT = 100;

/** Tamaño de pasada que pide la grilla cuando el usuario no elige otro. */
export const COVER_SYNC_DEFAULT_LIMIT = 25;

export type LibraryCoverSyncReport = {
  /** Juegos canónicos de la biblioteca sin portada (se cuentan juegos, no filas). */
  readonly missing: number;
  /** De los anteriores, los que no tienen appid de Steam conocido: ahí solo sirve la elección manual. */
  readonly missingWithoutSteamId: number;
  readonly updated: number;
  readonly failed: number;
  /** Con appid conocido que quedaron para la siguiente pasada, porque la pasada es acotada. */
  readonly remaining: number;
};

type UnknownRecord = Record<string, unknown>;

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function toCount(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : null;
}

/**
 * Cuerpo válido de una pasada: `{}` (el servidor aplica su default) o un `limit` entero 1..100. `null`
 * significa cuerpo inválido y la ruta responde 400: un límite fuera de rango nunca se recorta en silencio.
 */
export function parseCoverSyncRequest(input: unknown): { readonly limit?: number } | null {
  if (!isRecord(input)) return null;
  if (input.limit === undefined) return {};

  const limit = input.limit;
  return typeof limit === "number" &&
    Number.isSafeInteger(limit) &&
    limit >= 1 &&
    limit <= COVER_SYNC_MAX_LIMIT
    ? { limit }
    : null;
}

/** Reporte de la pasada, o `null` si algún contador falta o no es un entero no negativo. */
export function normalizeCoverSyncReport(input: unknown): LibraryCoverSyncReport | null {
  if (!isRecord(input)) return null;

  const missing = toCount(input.missing);
  const missingWithoutSteamId = toCount(input.missingWithoutSteamId);
  const updated = toCount(input.updated);
  const failed = toCount(input.failed);
  const remaining = toCount(input.remaining);

  if (
    missing === null ||
    missingWithoutSteamId === null ||
    updated === null ||
    failed === null ||
    remaining === null
  ) {
    return null;
  }

  return { missing, missingWithoutSteamId, updated, failed, remaining };
}

/** Catálogo donde se busca y se resuelve la portada: Steam (PC) o IGDB (consola). */
export type CoverSource = "steam" | "igdb";

/**
 * Elección manual de portada: exactamente UNO de los dos ids, nunca los dos ni ninguno, y jamás una URL
 * o un título. El servidor relee la portada por este id, así que un llamador no puede apuntar el catálogo
 * hacia una imagen arbitraria.
 */
export type CoverPick =
  | { readonly steamAppId: number }
  | { readonly igdbId: number };

/**
 * Fuente que corresponde a un grupo de plataformas según sus tiendas. Un grupo solo de PC (todas con
 * llave de `toStoreKey`) busca en Steam; uno solo de consola (ninguna con esa llave) busca en IGDB; un
 * grupo mixto —o uno sin plataformas— devuelve `null` y el usuario elige a mano. La portada es una sola
 * para todo el grupo, así que una fuente no se adivina a partir de la primera fila.
 */
export function resolveCoverSource(stores: readonly string[]): CoverSource | null {
  let hasPc = false;
  let hasNonPc = false;

  for (const store of stores) {
    if (toStoreKey(store) !== null) hasPc = true;
    else hasNonPc = true;
  }

  if (hasPc && hasNonPc) return null;
  if (hasPc) return "steam";
  if (hasNonPc) return "igdb";
  return null;
}

// Los dos y únicos campos que el backend acepta en el cuerpo. Cualquier otra llave es 400 allá
// (`JsonExtensionData` existe solo para rechazarla), así que se descarta aquí antes de gastar el viaje.
const COVER_PICK_FIELDS = new Set(["steamAppId", "igdbId"]);

function toPositiveId(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : null;
}

// Un campo "no enviado" es `undefined` o `null`: la misma convención que usa el backend.
function isAbsent(value: unknown): boolean {
  return value === undefined || value === null;
}

/**
 * Cuerpo válido de una elección manual, o `null`. Es estricto en las dos direcciones: solo los campos
 * `steamAppId`/`igdbId`, exactamente uno de ellos presente con un id entero positivo (el otro ausente o
 * nulo) y nada más. Los dos a la vez, ninguno, un id que no sea número entero positivo o un campo
 * desconocido devuelven `null`, igual que un 400 sin nada escrito en el backend.
 */
export function parseCoverPick(input: unknown): CoverPick | null {
  if (!isRecord(input)) return null;

  for (const key of Object.keys(input)) {
    if (!COVER_PICK_FIELDS.has(key)) return null;
  }

  const rawSteamAppId = input.steamAppId;
  const rawIgdbId = input.igdbId;
  const hasSteamAppId = !isAbsent(rawSteamAppId);
  const hasIgdbId = !isAbsent(rawIgdbId);

  // Los dos a la vez es ambiguo y ninguno no elige nada: los dos casos se rechazan, no se recortan.
  if (hasSteamAppId === hasIgdbId) return null;

  if (hasSteamAppId) {
    const steamAppId = toPositiveId(rawSteamAppId);
    return steamAppId === null ? null : { steamAppId };
  }

  const igdbId = toPositiveId(rawIgdbId);
  return igdbId === null ? null : { igdbId };
}

/** URL de portada devuelta por el servidor, o `null` si no vino ninguna. */
export function normalizeCoverUrl(input: unknown): string | null {
  if (!isRecord(input)) return null;
  const imageUrl = input.imageUrl;
  return typeof imageUrl === "string" && imageUrl.trim() !== "" ? imageUrl : null;
}
