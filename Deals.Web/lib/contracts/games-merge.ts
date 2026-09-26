// Contrato de la herramienta de mantenimiento `/library/duplicates`: grupos de juegos canónicos
// duplicados y la fusión manual entre ellos. Rutas relativas a propósito: este módulo se cubre con
// `node --test`, que no resuelve el alias `@/`.
import { toStoreKey } from "./stores.ts";

type UnknownRecord = Record<string, unknown>;

// --- Contrato congelado del backend ---

export type DuplicateStoreRef = {
  readonly store: string;
  readonly storeGameId: string;
};

export type DuplicateMember = {
  readonly gameId: number;
  readonly title: string;
  readonly stores: readonly DuplicateStoreRef[];
  readonly steamAppIds: readonly string[];
  readonly blocked: boolean;
  readonly blockReason: string | null;
};

// `members` siempre trae 2 o más: un grupo de uno no es un duplicado.
export type DuplicateGroup = {
  readonly foldedTitle: string;
  readonly blocked: boolean;
  readonly members: readonly DuplicateMember[];
};

export type MergeRequestPayload = {
  readonly intoGameId: number;
};

export type CrossStateStoreRow = {
  readonly store: string; readonly storeGameId: string; readonly state: string; readonly title: string;
  readonly priority: number | null; readonly isInstalled: boolean | null;
};
export type CrossStateExternalId = { readonly namespace: string; readonly externalId: string };
export type CrossStateMember = {
  readonly gameId: number; readonly title: string; readonly normalizedTitle: string; readonly type: string | null;
  readonly releaseYear: number | null; readonly states: readonly string[]; readonly storeRows: readonly CrossStateStoreRow[];
  readonly externalIds: readonly CrossStateExternalId[]; readonly steamAppIds: readonly string[];
  readonly evidence: readonly string[]; readonly blocked: boolean; readonly blockReason: string | null;
};
export type CrossStateCandidateGroup = {
  readonly candidateKey: string; readonly confidence: string; readonly reasons: readonly string[]; readonly warnings: readonly string[];
  readonly blocked: boolean; readonly blockReason: string | null; readonly members: readonly CrossStateMember[];
};

// 200: la fusión se aplicó. 409: el backend la bloqueó (solo por ambigüedad de identidad de Steam).
export type MergeApplied = {
  readonly kind: "applied";
  readonly movedExternalIds: number | null;
  readonly movedSteamGames: number | null;
  readonly movedLibraryRows: number | null;
};

export type MergeBlocked = {
  readonly kind: "blocked";
  readonly blockReason: string | null;
};

export type MergeResult = MergeApplied | MergeBlocked;

// --- Normalización ---

// Topes defensivos: esta pantalla es una herramienta de mantenimiento, no una vista de catálogo.
const MAX_GROUPS = 2_000;
const MAX_MEMBERS = 32;
const MAX_STORE_REFS = 32;
const MAX_STEAM_APP_IDS = 32;
const MAX_TITLE_LENGTH = 256;
const MAX_ID_LENGTH = 128;
const MAX_REASON_LENGTH = 2_000;

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

// Un contador del reporte de fusión: entero seguro y no negativo, o `null` si no llegó. Nunca se inventa
// un 0: la UI prefiere decir "no informado" antes que afirmar que no se movió nada.
function toCounter(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

// La tienda se canoniza con el vocabulario compartido (`toStoreKey`): "Epic Games" y "epic" son la misma
// llave. Una tienda fuera del vocabulario no se descarta —el grupo puede traer namespaces como `itad`—
// así que conserva su texto en minúsculas y `storeLabel` lo pinta tal cual.
function toCanonicalStore(value: unknown): string | null {
  const text = toBoundedText(value, 64)?.toLowerCase();
  if (text === undefined) return null;
  return toStoreKey(text) ?? text;
}

function normalizeStoreRef(value: unknown): DuplicateStoreRef | null {
  if (!isRecord(value)) return null;

  const store = toCanonicalStore(read(value, "store"));
  const storeGameId = toBoundedText(read(value, "storeGameId"), MAX_ID_LENGTH);
  if (store === null || storeGameId === null) return null;

  return { store, storeGameId };
}

// Los appids llegan como texto; un número entero positivo se tolera y se pasa a texto.
function toSteamAppId(value: unknown): string | null {
  if (typeof value === "number") return Number.isSafeInteger(value) && value > 0 ? String(value) : null;
  return toBoundedText(value, MAX_ID_LENGTH);
}

function normalizeCrossStateMember(value: unknown): CrossStateMember | null {
  if (!isRecord(value)) return null;
  const gameId = toPositiveInteger(read(value, "gameId"));
  const title = toBoundedText(read(value, "title"), MAX_TITLE_LENGTH);
  const normalizedTitle = toBoundedText(read(value, "normalizedTitle"), MAX_TITLE_LENGTH);
  if (gameId === null || title === null || normalizedTitle === null) return null;
  const list = (key: string, max: number): string[] => {
    const raw = read(value, key); if (!Array.isArray(raw)) return [];
    return raw.filter((x): x is string => typeof x === "string" && x.trim().length > 0).map(x => x.trim().slice(0, MAX_REASON_LENGTH)).slice(0, max);
  };
  const rawStores = read(value, "storeRows");
  const storeRows = Array.isArray(rawStores) ? rawStores.flatMap(x => {
    if (!isRecord(x)) return [];
    const store = toCanonicalStore(read(x, "store")); const id = toBoundedText(read(x, "storeGameId"), MAX_ID_LENGTH);
    const state = toBoundedText(read(x, "state"), 32); const rowTitle = toBoundedText(read(x, "title"), MAX_TITLE_LENGTH);
    const priorityValue = read(x, "priority"); const priority = typeof priorityValue === "number" && Number.isSafeInteger(priorityValue) ? priorityValue : null;
    const installedValue = read(x, "isInstalled"); const isInstalled = typeof installedValue === "boolean" ? installedValue : null;
    return store && id && state && rowTitle ? [{ store, storeGameId: id, state, title: rowTitle, priority, isInstalled }] : [];
  }).slice(0, MAX_STORE_REFS) : [];
  const rawIds = read(value, "externalIds");
  const externalIds = Array.isArray(rawIds) ? rawIds.flatMap(x => {
    if (!isRecord(x)) return [];
    const namespace = toBoundedText(read(x, "namespace"), 32); const externalId = toBoundedText(read(x, "externalId"), MAX_ID_LENGTH);
    return namespace && externalId ? [{ namespace, externalId }] : [];
  }).slice(0, MAX_STORE_REFS) : [];
  const year = read(value, "releaseYear");
  return { gameId, title, normalizedTitle, type: toBoundedText(read(value, "type"), 32), releaseYear: typeof year === "number" && Number.isInteger(year) ? year : null,
    states: list("states", 8), storeRows, externalIds, steamAppIds: list("steamAppIds", MAX_STEAM_APP_IDS), evidence: list("evidence", 16),
    blocked: read(value, "blocked") === true, blockReason: toBoundedText(read(value, "blockReason"), MAX_REASON_LENGTH) };
}

function normalizeMember(value: unknown): DuplicateMember | null {
  if (!isRecord(value)) return null;

  const gameId = toPositiveInteger(read(value, "gameId"));
  const title = toBoundedText(read(value, "title"), MAX_TITLE_LENGTH);
  if (gameId === null || title === null) return null;

  const stores: DuplicateStoreRef[] = [];
  const rawStores = read(value, "stores");
  if (Array.isArray(rawStores)) {
    for (const entry of rawStores) {
      const ref = normalizeStoreRef(entry);
      if (ref === null) continue;
      if (stores.some((existing) => existing.store === ref.store && existing.storeGameId === ref.storeGameId)) continue;
      stores.push(ref);
      if (stores.length === MAX_STORE_REFS) break;
    }
  }

  const steamAppIds: string[] = [];
  const rawAppIds = read(value, "steamAppIds");
  if (Array.isArray(rawAppIds)) {
    for (const entry of rawAppIds) {
      const appId = toSteamAppId(entry);
      if (appId === null || steamAppIds.includes(appId)) continue;
      steamAppIds.push(appId);
      if (steamAppIds.length === MAX_STEAM_APP_IDS) break;
    }
  }

  return {
    gameId,
    title,
    stores,
    steamAppIds,
    // Solo el literal `true` marca bloqueado: un `"true"` o un `1` no pueden deshabilitar una acción.
    blocked: read(value, "blocked") === true,
    blockReason: toBoundedText(read(value, "blockReason"), MAX_REASON_LENGTH)
  };
}

function normalizeGroup(value: unknown): DuplicateGroup | null {
  if (!isRecord(value)) return null;

  const foldedTitle = toBoundedText(read(value, "foldedTitle"), MAX_TITLE_LENGTH);
  if (foldedTitle === null) return null;

  const rawMembers = read(value, "members");
  if (!Array.isArray(rawMembers)) return null;

  const members: DuplicateMember[] = [];
  for (const entry of rawMembers) {
    const member = normalizeMember(entry);
    if (member === null) continue;
    members.push(member);
    if (members.length === MAX_MEMBERS) break;
  }

  // Contrato: un grupo con menos de dos miembros válidos no es un duplicado y se descarta (nunca se
  // rellena con miembros inválidos para llegar al mínimo).
  if (members.length < 2) return null;

  return { foldedTitle, blocked: read(value, "blocked") === true, members };
}

/**
 * Lista de grupos de duplicados. Un cuerpo que no es arreglo es `null` (respuesta inválida), nunca una
 * lista vacía: para una herramienta de mantenimiento, una forma rota no puede leerse como "no hay
 * duplicados". Los grupos malformados o con menos de dos miembros válidos se descartan uno a uno.
 */
export function normalizeCrossStateCandidates(input: unknown): CrossStateCandidateGroup[] | null {
  if (!Array.isArray(input)) return null;
  const result: CrossStateCandidateGroup[] = [];
  for (const item of input) {
    if (!isRecord(item)) continue;
    const candidateKey = toBoundedText(read(item, "candidateKey"), MAX_ID_LENGTH);
    const confidence = toBoundedText(read(item, "confidence"), 16);
    const rawMembers = read(item, "members");
    if (!candidateKey || !confidence || !Array.isArray(rawMembers)) continue;
    const members = rawMembers.flatMap((entry): CrossStateMember[] => { const member = normalizeCrossStateMember(entry); return member ? [member] : []; }).slice(0, MAX_MEMBERS);
    if (members.length < 2) continue;
    const strings = (key: string) => { const raw = read(item, key); return Array.isArray(raw) ? raw.filter((x): x is string => typeof x === "string").map(x => x.slice(0, MAX_REASON_LENGTH)).slice(0, 32) : []; };
    result.push({ candidateKey, confidence, reasons: strings("reasons"), warnings: strings("warnings"), blocked: read(item, "blocked") === true,
      blockReason: toBoundedText(read(item, "blockReason"), MAX_REASON_LENGTH), members });
    if (result.length === MAX_GROUPS) break;
  }
  return result;
}

export function normalizeDuplicateGroups(input: unknown): DuplicateGroup[] | null {
  if (!Array.isArray(input)) return null;

  const groups: DuplicateGroup[] = [];
  for (const entry of input) {
    const group = normalizeGroup(entry);
    if (group === null) continue;
    groups.push(group);
    if (groups.length === MAX_GROUPS) break;
  }
  return groups;
}

/**
 * Resultado de una fusión. `applied` decide la rama: `true` es la fusión hecha (200) y `false` es el
 * bloqueo con su razón (409). Cualquier otra cosa es una respuesta que no se puede interpretar y
 * devuelve `null`.
 */
export function normalizeMergeResult(input: unknown): MergeResult | null {
  if (!isRecord(input)) return null;

  const applied = read(input, "applied");

  if (applied === true) {
    return {
      kind: "applied",
      movedExternalIds: toCounter(read(input, "movedExternalIds")),
      movedSteamGames: toCounter(read(input, "movedSteamGames")),
      movedLibraryRows: toCounter(read(input, "movedLibraryRows"))
    };
  }

  if (applied === false) {
    return {
      kind: "blocked",
      blockReason: toBoundedText(read(input, "blockReason"), MAX_REASON_LENGTH)
    };
  }

  return null;
}

/** Cuerpo válido de una fusión, o `null`. `intoGameId` es obligatorio. */
export function parseMergeRequest(input: unknown): MergeRequestPayload | null {
  if (!isRecord(input)) return null;

  const intoGameId = toPositiveInteger(read(input, "intoGameId"));
  return intoGameId === null ? null : { intoGameId };
}
