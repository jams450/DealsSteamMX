import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  normalizeDuplicateGroups,
  normalizeMergeResult,
  type DuplicateGroup,
  type MergeResult
} from "@/lib/contracts/games-merge";

const INVALID_GROUPS = "El servidor devolvió una lista de duplicados inválida";
const INVALID_MERGE = "El servidor devolvió un resultado de fusión inválido";

export async function listDuplicateGroups(): Promise<readonly DuplicateGroup[]> {
  const response = await fetch("/api/bff/games/merge-suggestions", { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudieron cargar los duplicados");

  const groups = normalizeDuplicateGroups(await response.json());
  if (groups === null) throw new Error(INVALID_GROUPS);
  return groups;
}

/**
 * Fusiona `absorbedGameId` dentro de `intoGameId`.
 *
 * Un 409 es una fusión rechazada (identidad de Steam ambigua), no un fallo de red: se normaliza igual
 * que el 200 y se devuelve como `kind: "blocked"`, con la razón. Cualquier otro status sí es un error
 * (400/404/5xx) y se lanza como error tipado del repo.
 */
export async function mergeGame(absorbedGameId: number, intoGameId: number): Promise<MergeResult> {
  const response = await csrfFetch(`/api/bff/games/${absorbedGameId}/merge`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ intoGameId }),
    cache: "no-store"
  });

  if (response.status !== 200 && response.status !== 409) {
    throw await parseApiError(response, "No se pudo fusionar el juego");
  }

  const result = normalizeMergeResult(await response.json().catch(() => null));
  if (result === null) throw new Error(INVALID_MERGE);
  return result;
}
