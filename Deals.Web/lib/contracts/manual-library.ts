// Contrato del alta manual de juegos de consola (docs/PLAN_CONSOLE.md §5): la búsqueda de IGDB trae
// portadas y años para elegir, los candidatos del catálogo aparecen cuando el título ya existe, y el
// alta devuelve un outcome. El backend es el único que escribe portadas: aquí solo se parsea lo que
// llega, nunca se arma ni se envía una URL.
import { PLATFORM_CATALOG_KEYS, storeLabel } from "./stores.ts";

export type ManualSearchPlatform = { id: number; name: string };

export type ManualSearchHit = {
  igdbId: number;
  title: string;
  releaseYear: number | null;
  imageUrl: string | null;
  platforms: ManualSearchPlatform[];
};

export type ManualSearchResult = {
  source: string | null;
  hits: ManualSearchHit[];
};

export type ManualCandidate = {
  gameId: number;
  title: string;
  releaseYear: number | null;
  imageUrl: string | null;
  inLibrary: boolean;
};

export type ManualAddOutcome = "created" | "attached" | "duplicate" | "candidates";

export type ManualAddResult = {
  outcome: ManualAddOutcome;
  created: number;
  attached: number;
  duplicateRow: number;
  gameId: number;
  userLibraryId: number;
  candidates: ManualCandidate[];
};

export type ManualAddInput = {
  store: string;
  title: string;
  gameId?: number;
  create?: boolean;
  igdbId?: number;
};

// Catálogo visible del selector del diálogo. Es cosmético: el valor viaja validado por `normalizeStore`
// (el mismo punto único que el backend), así que una plataforma que falte aquí se escribe igual en
// «Otra plataforma…» sin tocar código.
export const MANUAL_PLATFORM_OPTIONS: readonly { value: string; label: string }[] = PLATFORM_CATALOG_KEYS.map(
  (value) => ({ value, label: storeLabel(value) })
);

// Marca local del <select> que abre el campo libre. Nunca viaja al servidor.
export const MANUAL_PLATFORM_OTHER = "other";

const OUTCOMES: readonly string[] = ["created", "attached", "duplicate", "candidates"];

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function text(value: unknown, maxLength: number): string | null {
  return typeof value === "string" && value.length > 0 && value.length <= maxLength ? value : null;
}

function integer(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : null;
}

function nullableInteger(value: unknown): number | null {
  return value === null || value === undefined ? null : integer(value);
}

// Solo URLs ya absolutadas por el servidor (el backend normaliza los `//images.igdb.com/...` de IGDB).
function httpsUrl(value: unknown): string | null {
  const candidate = text(value, 2048);
  return candidate !== null && /^https?:\/\//.test(candidate) ? candidate : null;
}

export function normalizeManualSearchResponse(input: unknown): ManualSearchResult | null {
  if (!isRecord(input)) return null;
  if (!Array.isArray(input.hits)) return null;

  const source = input.source === null || input.source === undefined ? null : text(input.source, 32);
  if (input.source !== null && input.source !== undefined && source === null) return null;

  const hits: ManualSearchHit[] = [];
  for (const raw of input.hits) {
    if (!isRecord(raw)) return null;
    const igdbId = integer(raw.igdbId);
    const title = text(raw.title, 512);
    if (igdbId === null || igdbId <= 0 || title === null) return null;

    const platforms: ManualSearchPlatform[] = [];
    if (Array.isArray(raw.platforms)) {
      for (const rawPlatform of raw.platforms) {
        if (!isRecord(rawPlatform)) return null;
        const id = integer(rawPlatform.id);
        const name = text(rawPlatform.name, 128);
        if (id === null || name === null) return null;
        platforms.push({ id, name });
      }
    }

    hits.push({ igdbId, title, releaseYear: nullableInteger(raw.releaseYear), imageUrl: httpsUrl(raw.imageUrl), platforms });
  }

  return { source, hits };
}

export function normalizeManualCandidatesResponse(input: unknown): ManualCandidate[] | null {
  if (!isRecord(input) || !Array.isArray(input.candidates)) return null;

  const candidates: ManualCandidate[] = [];
  for (const raw of input.candidates) {
    if (!isRecord(raw)) return null;
    const gameId = integer(raw.gameId);
    const title = text(raw.title, 512);
    if (gameId === null || gameId <= 0 || title === null) return null;
    candidates.push({
      gameId,
      title,
      releaseYear: nullableInteger(raw.releaseYear),
      imageUrl: httpsUrl(raw.imageUrl),
      inLibrary: raw.inLibrary === true
    });
  }

  return candidates;
}

export function normalizeManualAddResponse(input: unknown): ManualAddResult | null {
  if (!isRecord(input)) return null;

  const outcome = text(input.outcome, 32);
  if (outcome === null || !OUTCOMES.includes(outcome)) return null;

  const created = integer(input.created);
  const attached = integer(input.attached);
  const duplicateRow = integer(input.duplicateRow);
  const gameId = integer(input.gameId);
  const userLibraryId = integer(input.userLibraryId);
  const candidates = normalizeManualCandidatesResponse({ candidates: input.candidates });
  if (created === null || attached === null || duplicateRow === null || gameId === null || userLibraryId === null || candidates === null) {
    return null;
  }

  return { outcome: outcome as ManualAddOutcome, created, attached, duplicateRow, gameId, userLibraryId, candidates };
}
