// Contrato del relleno de portadas de la biblioteca. El cliente manda un id de proveedor — el appid de
// Steam, el id de IGDB o el id de SteamGridDB, exactamente uno de los tres —, nunca una URL: la portada la
// resuelve y la guarda el servidor, igual que el resto de datos de proveedor. La elección es tan estricta como
// la del backend: dos o más campos a la vez, ninguno, o un tercer campo (`imageUrl`, `title`) es inválido
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
  /** Portadas que la pasada llenó, sumando las tres fuentes. */
  readonly updated: number;
  /** De `updated`, las que resolvió Steam (por appid ya conocido o por búsqueda de título). */
  readonly updatedBySteam: number;
  /** De `updated`, las que resolvió la búsqueda por título en IGDB. */
  readonly updatedByIgdb: number;
  /** De `updated`, las que resolvió SteamGridDB. */
  readonly updatedBySteamGridDb: number;
  /** Juegos que la cadena completa respondió sin una URL usable: no había arte, nada está roto. */
  readonly unmatched: number;
  /** Juegos donde alguna fuente no se pudo consultar (transporte, sin configurar, carga ilegible). */
  readonly failed: number;
  /** Juegos que la pasada no visitó porque llegó a su tope: quedan para la siguiente. */
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
  const updated = toCount(input.updated);
  const updatedBySteam = toCount(input.updatedBySteam);
  const updatedByIgdb = toCount(input.updatedByIgdb);
  const updatedBySteamGridDb = toCount(input.updatedBySteamGridDb);
  const unmatched = toCount(input.unmatched);
  const failed = toCount(input.failed);
  const remaining = toCount(input.remaining);

  if (
    missing === null ||
    updated === null ||
    updatedBySteam === null ||
    updatedByIgdb === null ||
    updatedBySteamGridDb === null ||
    unmatched === null ||
    failed === null ||
    remaining === null
  ) {
    return null;
  }

  return { missing, updated, updatedBySteam, updatedByIgdb, updatedBySteamGridDb, unmatched, failed, remaining };
}

/** Catálogo donde se busca y se resuelve la portada: Steam (PC), IGDB o SteamGridDB (arte de la comunidad). */
export type CoverSource = "steam" | "igdb" | "steamgriddb";

/**
 * Elección manual de portada: exactamente UNO de los tres ids, nunca dos ni ninguno, y jamás una URL o un
 * título. El servidor relee la portada por este id, así que un llamador no puede apuntar el catálogo hacia
 * una imagen arbitraria.
 */
export type CoverPick =
  | { readonly steamAppId: number }
  | { readonly igdbId: number }
  | { readonly steamGridDbId: number };

/**
 * Fuente que corresponde a un grupo de plataformas según sus tiendas, como fuente **preferida**: un grupo solo
 * de PC (todas con llave de `toStoreKey`) propone Steam; uno solo de consola (ninguna con esa llave) propone
 * IGDB; un grupo mixto —o uno sin plataformas— devuelve `null` y no propone ninguna. Es solo el punto de
 * partida del selector manual, que ofrece siempre las tres fuentes: con SteamGridDB (arte de la comunidad, sin
 * plataforma) un grupo solo de PC también necesita una vía que no sea Steam. La portada es una sola para todo
 * el grupo, así que la fuente preferida no se adivina a partir de la primera fila.
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

// Los tres y únicos campos que el backend acepta en el cuerpo. Cualquier otra llave es 400 allá
// (`JsonExtensionData` existe solo para rechazarla), así que se descarta aquí antes de gastar el viaje.
const COVER_PICK_FIELDS = new Set(["steamAppId", "igdbId", "steamGridDbId"]);

function toPositiveId(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : null;
}

// Un campo "no enviado" es `undefined` o `null`: la misma convención que usa el backend.
function isAbsent(value: unknown): boolean {
  return value === undefined || value === null;
}

/**
 * Cuerpo válido de una elección manual, o `null`. Es estricto en las dos direcciones: solo los campos
 * `steamAppId`/`igdbId`/`steamGridDbId`, exactamente uno de ellos presente con un id entero positivo (los
 * otros ausentes o nulos) y nada más. Dos o más a la vez, ninguno, un id que no sea número entero positivo o
 * un campo desconocido devuelven `null`, igual que un 400 sin nada escrito en el backend.
 */
export function parseCoverPick(input: unknown): CoverPick | null {
  if (!isRecord(input)) return null;

  for (const key of Object.keys(input)) {
    if (!COVER_PICK_FIELDS.has(key)) return null;
  }

  const rawSteamAppId = input.steamAppId;
  const rawIgdbId = input.igdbId;
  const rawSteamGridDbId = input.steamGridDbId;
  const hasSteamAppId = !isAbsent(rawSteamAppId);
  const hasIgdbId = !isAbsent(rawIgdbId);
  const hasSteamGridDbId = !isAbsent(rawSteamGridDbId);

  // Dos o más a la vez es ambiguo y ninguno no elige nada: los dos casos se rechazan, no se recortan.
  const provided = (hasSteamAppId ? 1 : 0) + (hasIgdbId ? 1 : 0) + (hasSteamGridDbId ? 1 : 0);
  if (provided !== 1) return null;

  if (hasSteamAppId) {
    const steamAppId = toPositiveId(rawSteamAppId);
    return steamAppId === null ? null : { steamAppId };
  }

  if (hasIgdbId) {
    const igdbId = toPositiveId(rawIgdbId);
    return igdbId === null ? null : { igdbId };
  }

  const steamGridDbId = toPositiveId(rawSteamGridDbId);
  return steamGridDbId === null ? null : { steamGridDbId };
}

/** Un candidato de la búsqueda por título de SteamGridDB: id para elegir, nombre y si es la fila verificada. */
export type SteamGridDbCoverCandidate = {
  readonly id: number;
  readonly name: string;
  readonly verified: boolean;
};

/**
 * Resultado de la búsqueda de candidatos en SteamGridDB. `source === null` significa «proveedor no
 * disponible», que el selector distingue de «sin candidatos»; la lista va vacía cuando el proveedor
 * respondió y no conoce ningún juego con ese título. No hay URL: el autocomplete no publica arte.
 */
export type SteamGridDbCoverSearch = {
  readonly source: string | null;
  readonly candidates: readonly SteamGridDbCoverCandidate[];
};

/**
 * Respuesta válida de la búsqueda en SteamGridDB, o `null` si la forma no es la documentada. Es estricta en
 * todo: `source` debe ser `null` o una cadena no vacía, cada candidato necesita un id entero positivo, un
 * nombre no vacío y un `verified` booleano. Un candidato roto invalida la respuesta entera en vez de recortar
 * la lista, porque un id que la UI no puede mandar de vuelta es un botón que falla al pulsarlo.
 */
export function normalizeSteamGridDbCoverSearch(input: unknown): SteamGridDbCoverSearch | null {
  if (!isRecord(input)) return null;

  // El API omite los miembros nulos (`DefaultIgnoreCondition = WhenWritingNull`), así que «proveedor no
  // disponible» llega como llave AUSENTE, no como `null` literal: ausente y nulo son la misma respuesta, igual
  // que en `normalizeManualSearchResponse`. Lo que sí invalida la carga es un `source` presente que no sea una
  // cadena con contenido.
  const rawSource = input.source;
  let source: string | null;
  if (rawSource === null || rawSource === undefined) {
    source = null;
  } else if (typeof rawSource === "string" && rawSource.trim() !== "") {
    source = rawSource;
  } else {
    return null;
  }

  const rawCandidates = input.candidates;
  if (!Array.isArray(rawCandidates)) return null;

  const candidates: SteamGridDbCoverCandidate[] = [];
  for (const raw of rawCandidates) {
    if (!isRecord(raw)) return null;

    const id = toPositiveId(raw.id);
    if (id === null) return null;

    const name = raw.name;
    if (typeof name !== "string" || name.trim() === "") return null;

    const verified = raw.verified;
    if (typeof verified !== "boolean") return null;

    candidates.push({ id, name, verified });
  }

  return { source, candidates };
}

/** URL de portada devuelta por el servidor, o `null` si no vino ninguna. */
export function normalizeCoverUrl(input: unknown): string | null {
  if (!isRecord(input)) return null;
  const imageUrl = input.imageUrl;
  return typeof imageUrl === "string" && imageUrl.trim() !== "" ? imageUrl : null;
}
