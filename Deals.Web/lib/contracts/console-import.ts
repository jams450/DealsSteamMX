// Contrato de la importación masiva de consolas (docs/PLAN_CONSOLE.md §7), el camino paralelo a la
// importación de tiendas:
//   POST /api/library/console-import/preview  → solo lectura: candidatos del catálogo + plataformas
//                                                sugeridas por los nombres de Playnite.
//   POST /api/library/console-import/commit   → una decisión explícita por entrada.
//
// Regla del plan que este módulo hace cumplir: nada aquí afirma identidad ni posesión. El título solo
// produce *candidatos* y `Platforms` solo produce *sugerencias*; la plataforma que se escribe y el juego
// al que se engancha la elige una persona en el diálogo. Por eso el borrador de decisión arranca en
// «crear juego nuevo» (nunca adjuntar el primer candidato) y en la primera plataforma sugerida (visible
// y cambiable, no oculta).
//
// Rutas relativas a propósito: `node --test` cubre este módulo y no resuelve el alias `@/`.
import { PLATFORM_CATALOG_KEYS, normalizeStore, storeLabel, toStoreKey } from "./stores.ts";

type UnknownRecord = Record<string, unknown>;

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

// El export de Playnite usa PascalCase (`GameId`, `Platforms`); se tolera camelCase porque el BFF y las
// pruebas escriben así. El backend acepta las dos formas (System.Text.Json es case-insensitive).
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

function toNonNegativeInteger(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

function toPositiveInteger(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

// Igual que la biblioteca: solo una portada https absoluta llega a un `src`.
function toHttpsUrl(value: unknown): string | null {
  const text = toText(value);
  if (text === null) return null;
  try {
    return new URL(text).protocol === "https:" ? text : null;
  } catch {
    return null;
  }
}

// --- Límites (espejo de `ConsoleImportLimits` del backend) -------------------------------------------

/**
 * `ConsoleImportLimits.MaxEntries` y el **único** tope de filas de este contrato. El preview se puede
 * trocear por tandas porque es solo lectura, pero el **commit es atómico** (todo-o-nada, 409 sin
 * escrituras si algo choca): una importación con más entradas importables que esto no se puede
 * completar desde el diálogo. Por eso el parser lo señala (`exceedsEntryLimit`) antes del preview en
 * vez de dejar que la persona resuelva miles de filas para toparse con el rechazo al final.
 */
export const CONSOLE_IMPORT_MAX_ENTRIES = 2000;

/** `user_library.title` es VARCHAR(256): un título más largo es un 400 en TODO el payload. */
export const CONSOLE_IMPORT_MAX_TITLE_LENGTH = 256;

// Mismo límite de archivo que la importación de Playnite: el export real pesa menos de 1 MiB.
export const CONSOLE_IMPORT_MAX_BYTES = 10 * 1024 * 1024;

// Marca local del <select> que abre el campo de plataforma libre. Nunca viaja al servidor.
export const CONSOLE_IMPORT_PLATFORM_OTHER = "other";

// --- Paso 1: el archivo de Playnite ----------------------------------------------------------------

export const CONSOLE_IMPORT_SKIP_REASONS = [
  "store_row",
  "missing_id",
  "missing_name",
  "title_too_long",
  "duplicate_id",
  "malformed"
] as const;

export type ConsoleImportSkipReason = (typeof CONSOLE_IMPORT_SKIP_REASONS)[number];

// Por qué una fila del archivo no entra al contrato de consola. Una fila de tienda no es un error: es
// la otra importación.
export const CONSOLE_IMPORT_SKIP_LABELS: Readonly<Record<ConsoleImportSkipReason, string>> = {
  store_row: "Es una fila de tienda (va a la importación normal)",
  missing_id: "Sin id de Playnite",
  missing_name: "Sin título",
  title_too_long: "Título de más de 256 caracteres",
  duplicate_id: "Id repetido en el archivo",
  malformed: "Fila ilegible"
};

export type ConsoleImportSkippedEntry = {
  readonly entryId: string | null;
  readonly name: string | null;
  readonly source: string | null;
  readonly reason: ConsoleImportSkipReason;
};

// Una entrada lista para `preview`/`commit`: `entryId` es el GameId de Playnite (la llave que liga el
// preview con su decisión) y `platforms` son los nombres de plataforma que Playnite trae, que el
// servidor usa SOLO para sugerir.
export type ConsoleImportEntryInput = {
  readonly entryId: string;
  readonly name: string;
  readonly platforms: readonly string[];
  readonly isInstalled: boolean | null;
  readonly added: string | null;
};

export type ConsoleImportFile = {
  /** Filas limpias, sin repetidos, listas para previsualizar. Nunca se recortan: si hay más que el
   *  tope, se conservan todas y `exceedsEntryLimit` lo dice. */
  readonly entries: readonly ConsoleImportEntryInput[];
  /** Todo lo que no entra, con su motivo: se muestra, nunca se envía. */
  readonly skipped: readonly ConsoleImportSkippedEntry[];
  /** Filas leídas del archivo: el total real, sin truncar. */
  readonly totalRows: number;
  /** Fechas ilegibles que se descartaron: la fila entra sin fecha, el payload no se cae. */
  readonly droppedDates: number;
  /** Hay más entradas importables que `CONSOLE_IMPORT_MAX_ENTRIES`: el commit atómico no puede
   *  aplicarse y el diálogo lo explica antes del preview. Ninguna fila queda oculta. */
  readonly exceedsEntryLimit: boolean;
};

// `Platforms` es metadato y en la práctica puede llegar como arreglo de textos o de objetos
// (`{ name }` según la versión de Playnite): se aceptan las dos formas y se deduplica.
function toPlatformNames(value: unknown): string[] {
  if (!Array.isArray(value)) return [];

  const names: string[] = [];
  for (const raw of value) {
    const name = typeof raw === "string" ? toBoundedText(raw, 128) : isRecord(raw) ? toBoundedText(read(raw, "name"), 128) : null;
    if (name === null || names.includes(name)) continue;
    names.push(name);
    if (names.length === 32) break;
  }
  return names;
}

// Playnite emite `/Date(<epoch>)/` o un ISO. Se reenvía TAL CUAL (el servidor lo parsea con
// `PlayniteDates`) pero se descarta lo que no tenga ninguna de las dos formas: `Added` inválido es un
// 400 en todo el payload, y una fecha es peor perderla que romper el archivo entero.
const DOTNET_DATE = /^\/Date\(-?\d+\)\/$/;
const ISO_DATE = /^\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)?$/;

function readAdded(value: unknown): { value: string | null; dropped: boolean } {
  const text = toText(value);
  if (text === null) return { value: null, dropped: false };
  if (DOTNET_DATE.test(text)) return { value: text, dropped: false };
  if (ISO_DATE.test(text) && !Number.isNaN(Date.parse(text.replace(" ", "T")))) return { value: text, dropped: false };
  return { value: null, dropped: true };
}

/**
 * Lee el JSON del export de Playnite y separa lo que este contrato puede importar de lo que no.
 * Devuelve `null` solo cuando la raíz no es un arreglo (archivo equivocado). Un arreglo vacío es un
 * archivo válido sin nada que importar, no un error.
 *
 * No trunca nunca: leer 10 MiB de JSON ya cabe en memoria, y recortar en silencio dejaría filas que
 * la persona nunca vería. Lo que sí hace es señalar `exceedsEntryLimit` cuando las entradas
 * importables superan el tope del commit atómico.
 */
export function parseConsoleImportFile(input: unknown): ConsoleImportFile | null {
  if (!Array.isArray(input)) return null;

  const entries: ConsoleImportEntryInput[] = [];
  const skipped: ConsoleImportSkippedEntry[] = [];
  const seen = new Set<string>();
  let droppedDates = 0;

  for (const row of input) {
    if (!isRecord(row)) {
      skipped.push({ entryId: null, name: null, source: null, reason: "malformed" });
      continue;
    }

    const entryId = toBoundedText(read(row, "gameId"), 128);
    const name = toText(read(row, "name"));
    const source = toBoundedText(read(row, "source"), 64);

    // Una fila con `Source` es de tienda: su contrato es `POST /api/library/import` y aquí sería un 400
    // de todo el payload. No es un error del archivo, es la otra puerta.
    if (source !== null) {
      skipped.push({ entryId, name, source, reason: "store_row" });
      continue;
    }
    if (entryId === null) {
      skipped.push({ entryId: null, name, source: null, reason: "missing_id" });
      continue;
    }
    if (name === null) {
      skipped.push({ entryId, name: null, source: null, reason: "missing_name" });
      continue;
    }
    if (name.length > CONSOLE_IMPORT_MAX_TITLE_LENGTH) {
      skipped.push({ entryId, name, source: null, reason: "title_too_long" });
      continue;
    }
    if (seen.has(entryId)) {
      skipped.push({ entryId, name, source: null, reason: "duplicate_id" });
      continue;
    }
    seen.add(entryId);

    const added = readAdded(read(row, "added"));
    if (added.dropped) droppedDates += 1;

    const isInstalled = read(row, "isInstalled");
    entries.push({
      entryId,
      name,
      platforms: toPlatformNames(read(row, "platforms")),
      isInstalled: typeof isInstalled === "boolean" ? isInstalled : null,
      added: added.value
    });
  }

  return {
    entries,
    skipped,
    totalRows: input.length,
    droppedDates,
    exceedsEntryLimit: entries.length > CONSOLE_IMPORT_MAX_ENTRIES
  };
}

// Cuerpo exacto del preview/commit: `source: null` explícito es el requisito del contrato de consola
// (una tienda aquí se rechaza), y la API ignora cualquier campo de más.
export function toConsoleImportEntryPayload(entry: ConsoleImportEntryInput): UnknownRecord {
  return {
    gameId: entry.entryId,
    source: null,
    name: entry.name,
    platforms: [...entry.platforms],
    isInstalled: entry.isInstalled,
    added: entry.added
  };
}

// --- Paso 2: el preview ----------------------------------------------------------------------------

export type ConsoleImportPlatformSuggestion = {
  readonly slug: string;
  readonly displayName: string;
  readonly source: string;
};

export type ConsoleImportCandidate = {
  readonly gameId: number;
  readonly title: string;
  readonly releaseYear: number | null;
  readonly imageUrl: string | null;
  readonly inLibrary: boolean;
};

export type ConsoleImportPreviewEntry = {
  readonly index: number;
  readonly entryId: string;
  readonly name: string;
  readonly candidates: readonly ConsoleImportCandidate[];
  readonly suggestedPlatforms: readonly ConsoleImportPlatformSuggestion[];
  readonly needsPlatform: boolean;
};

export type ConsoleImportPreview = {
  readonly entries: readonly ConsoleImportPreviewEntry[];
};

function normalizeCandidate(value: unknown): ConsoleImportCandidate | null {
  if (!isRecord(value)) return null;

  const gameId = toPositiveInteger(value.gameId);
  const title = toBoundedText(value.title, 512);
  if (gameId === null || title === null) return null;

  return {
    gameId,
    title,
    releaseYear: toPositiveInteger(value.releaseYear),
    imageUrl: toHttpsUrl(value.imageUrl),
    inLibrary: value.inLibrary === true
  };
}

function normalizeSuggestion(value: unknown): ConsoleImportPlatformSuggestion | null {
  if (!isRecord(value)) return null;

  const slug = toBoundedText(value.slug, 32);
  const displayName = toBoundedText(value.displayName, 128);
  const source = toBoundedText(value.source, 128);
  if (slug === null || displayName === null || source === null) return null;

  return { slug, displayName, source };
}

// Estricto a propósito: una entrada malformada no se descarta en silencio (a diferencia de las filas de
// la biblioteca, que son solo pintura). Aquí cada entrada gobierna una escritura, así que una respuesta
// que no cuadra se rechaza entera y la UI muestra el error en vez de omitir una fila.
export function normalizeConsoleImportPreview(input: unknown): ConsoleImportPreview | null {
  if (!isRecord(input) || !Array.isArray(input.entries)) return null;
  if (input.entries.length > CONSOLE_IMPORT_MAX_ENTRIES) return null;

  const entries: ConsoleImportPreviewEntry[] = [];
  for (const raw of input.entries) {
    if (!isRecord(raw)) return null;

    const index = toNonNegativeInteger(raw.index);
    const entryId = toBoundedText(raw.entryId, 128);
    const name = toBoundedText(raw.name, CONSOLE_IMPORT_MAX_TITLE_LENGTH);
    if (index === null || entryId === null || name === null) return null;

    if (!Array.isArray(raw.candidates) || !Array.isArray(raw.suggestedPlatforms)) return null;

    const candidates: ConsoleImportCandidate[] = [];
    for (const rawCandidate of raw.candidates) {
      const candidate = normalizeCandidate(rawCandidate);
      if (candidate === null) return null;
      candidates.push(candidate);
    }

    const suggestedPlatforms: ConsoleImportPlatformSuggestion[] = [];
    for (const rawSuggestion of raw.suggestedPlatforms) {
      const suggestion = normalizeSuggestion(rawSuggestion);
      if (suggestion === null) return null;
      // Una sugerencia que no es un slug válido no puede llegar al <select>: se descarta antes.
      if (normalizeStore(suggestion.slug) !== suggestion.slug) continue;
      // Una tienda de PC nunca es plataforma de consola: tampoco llega al <select>.
      if (isConsoleImportStorePlatform(suggestion.slug)) continue;
      suggestedPlatforms.push(suggestion);
    }

    entries.push({
      index,
      entryId,
      name,
      candidates,
      suggestedPlatforms,
      needsPlatform: raw.needsPlatform === true || suggestedPlatforms.length === 0
    });
  }

  return { entries };
}

// --- Paso 3: la decisión y el commit ----------------------------------------------------------------

export type ConsoleImportDecision = {
  readonly entryId: string;
  readonly name: string;
  readonly ownedPlatform: string;
  readonly attachGameId: number | null;
  readonly create: boolean;
  readonly isInstalled: boolean | null;
  readonly added: string | null;
};

// Estado editable de una fila del preview. `platform` es un slug (o ""), `attachGameId === null`
// significa «crear juego nuevo» — el default seguro: nunca se afirma identidad por título.
export type ConsoleImportDecisionDraft = {
  readonly entryId: string;
  readonly name: string;
  readonly platform: string;
  readonly attachGameId: number | null;
};

/**
 * Borrador inicial de una entrada: la primera plataforma que el catálogo pudo sugerir (visible y
 * cambiable) y «crear juego nuevo», que es la única acción que no afirma ninguna identidad. Un
 * candidato existente se ofrece aparte y se elige a mano.
 */
export function initialConsoleImportDraft(entry: ConsoleImportPreviewEntry): ConsoleImportDecisionDraft {
  const suggested = entry.suggestedPlatforms.find((platform) => platform.slug !== CONSOLE_IMPORT_PLATFORM_OTHER);
  return {
    entryId: entry.entryId,
    name: entry.name,
    platform: suggested?.slug ?? "",
    attachGameId: null
  };
}

/**
 * `true` cuando el texto es una tienda de PC (`StoreKeys`): las ocho llaves y sus alias (`Steam`,
 * `Xbox`, `Ubisoft Connect`, `Battle.net`…). El backend rechaza cualquiera de ellas como
 * `ownedPlatform` del camino de consola (§7.4: «no toca PC»), así que la UI no puede marcarlas como
 * listas. Ojo: `xbox-one`, `series-s` o `xbox360` **no** son llaves de tienda y sí son consolas.
 */
export function isConsoleImportStorePlatform(value: string): boolean {
  return toStoreKey(value) !== null;
}

/**
 * Plataforma resuelta de un borrador, o `null` si aún no hay una elección válida. La marca
 * «Otra plataforma…» no es un slug: mientras el campo libre esté vacío, la fila NO está lista (si se
 * normalizara el texto de la marca, una fila sin plataforma viajaría con `ownedPlatform="other"`).
 * Una tienda de PC se rechaza aquí: `normalizeStore` la aceptaría como slug y el commit devolvería 400.
 */
export function resolveConsoleImportPlatform(draft: ConsoleImportDecisionDraft): string | null {
  const value = draft.platform.trim();
  if (value.length === 0 || value === CONSOLE_IMPORT_PLATFORM_OTHER) return null;
  if (isConsoleImportStorePlatform(value)) return null;
  return normalizeStore(value);
}

/** Una fila está lista para commitear cuando su plataforma es un slug válido. */
export function isConsoleImportDraftReady(draft: ConsoleImportDecisionDraft): boolean {
  return resolveConsoleImportPlatform(draft) !== null;
}

export function buildConsoleImportDecision(
  draft: ConsoleImportDecisionDraft,
  source: ConsoleImportEntryInput
): ConsoleImportDecision | null {
  const platform = resolveConsoleImportPlatform(draft);
  if (platform === null) return null;

  return {
    entryId: draft.entryId,
    name: source.name,
    ownedPlatform: platform,
    attachGameId: draft.attachGameId,
    // Exactamente una acción de identidad, como exige el backend.
    create: draft.attachGameId === null,
    isInstalled: source.isInstalled,
    added: source.added
  };
}

export type ConsoleImportPlatformOption = {
  readonly value: string;
  readonly label: string;
};

/**
 * Opciones del selector de plataforma de una fila: primero las que el catálogo sugirió para esa entrada
 * (con su nombre visible) y después el catálogo de consolas que aún no esté sugerido. Una consola que
 * no esté en ninguna de las dos listas se escribe en «Otra plataforma…».
 */
export function consoleImportPlatformGroups(suggestions: readonly ConsoleImportPlatformSuggestion[]): {
  suggested: ConsoleImportPlatformOption[];
  catalog: ConsoleImportPlatformOption[];
} {
  const suggested: ConsoleImportPlatformOption[] = [];
  const seen = new Set<string>();

  for (const suggestion of suggestions) {
    if (seen.has(suggestion.slug)) continue;
    // La marca de «Otra plataforma…» está reservada: no puede llegar como valor de una opción.
    if (suggestion.slug === CONSOLE_IMPORT_PLATFORM_OTHER) continue;
    // Una tienda de PC no es una consola: no puede ofrecerse como opción.
    if (isConsoleImportStorePlatform(suggestion.slug)) continue;
    seen.add(suggestion.slug);
    suggested.push({ value: suggestion.slug, label: suggestion.displayName });
  }

  const catalog: ConsoleImportPlatformOption[] = [];
  for (const key of PLATFORM_CATALOG_KEYS) {
    if (seen.has(key)) continue;
    seen.add(key);
    catalog.push({ value: key, label: storeLabel(key) });
  }

  return { suggested, catalog };
}

export const CONSOLE_IMPORT_OUTCOMES = ["created", "attached", "already_present"] as const;

export type ConsoleImportOutcome = (typeof CONSOLE_IMPORT_OUTCOMES)[number];

export type ConsoleImportCommitEntry = {
  readonly entryId: string;
  readonly outcome: ConsoleImportOutcome;
  readonly platform: string;
  readonly gameId: number;
  readonly userLibraryId: number | null;
};

export type ConsoleImportConflict = {
  readonly entryId: string;
  readonly platform: string;
  readonly gameId: number;
  readonly ownerGameId: number;
};

export type ConsoleImportCommitResult = {
  /** `false` = commit rechazado (409) sin escribir nada; `conflicts` lo explica. */
  readonly applied: boolean;
  readonly created: number;
  readonly attached: number;
  readonly alreadyPresent: number;
  readonly entries: readonly ConsoleImportCommitEntry[];
  readonly conflicts: readonly ConsoleImportConflict[];
};

export function normalizeConsoleImportCommit(input: unknown): ConsoleImportCommitResult | null {
  if (!isRecord(input)) return null;

  const applied = input.applied;
  const created = toNonNegativeInteger(input.created);
  const attached = toNonNegativeInteger(input.attached);
  const alreadyPresent = toNonNegativeInteger(input.alreadyPresent);
  if (typeof applied !== "boolean" || created === null || attached === null || alreadyPresent === null) return null;

  if (!Array.isArray(input.entries) || !Array.isArray(input.conflicts)) return null;

  const entries: ConsoleImportCommitEntry[] = [];
  for (const raw of input.entries) {
    if (!isRecord(raw)) return null;

    const entryId = toBoundedText(raw.entryId, 128);
    const outcome = toText(raw.outcome);
    const platform = toBoundedText(raw.platform, 32);
    const gameId = toPositiveInteger(raw.gameId);
    if (entryId === null || outcome === null || platform === null || gameId === null) return null;
    // Un outcome desconocido sería un desajuste de versión: se rechaza el reporte entero antes que
    // pintarlo con la etiqueta equivocada.
    if (!CONSOLE_IMPORT_OUTCOMES.includes(outcome as ConsoleImportOutcome)) return null;

    const rawUserLibraryId = raw.userLibraryId;
    const userLibraryId = rawUserLibraryId === null || rawUserLibraryId === undefined ? null : toPositiveInteger(rawUserLibraryId);

    entries.push({ entryId, outcome: outcome as ConsoleImportOutcome, platform, gameId, userLibraryId });
  }

  const conflicts: ConsoleImportConflict[] = [];
  for (const raw of input.conflicts) {
    if (!isRecord(raw)) return null;

    const entryId = toBoundedText(raw.entryId, 128);
    const platform = toBoundedText(raw.platform, 32);
    const gameId = toPositiveInteger(raw.gameId);
    const ownerGameId = toPositiveInteger(raw.ownerGameId);
    if (entryId === null || platform === null || gameId === null || ownerGameId === null) return null;

    conflicts.push({ entryId, platform, gameId, ownerGameId });
  }

  return { applied, created, attached, alreadyPresent, entries, conflicts };
}
