import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import { normalizeCrossStateCandidates, normalizeDuplicateGroups, normalizeMergeResult, type CrossStateCandidateGroup, type DuplicateGroup, type MergeResult } from "@/lib/contracts/games-merge";

export async function listDuplicateGroups(): Promise<readonly DuplicateGroup[]> {
  const response = await fetch("/api/bff/games/merge-suggestions", { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudieron cargar los duplicados");
  const groups = normalizeDuplicateGroups(await response.json());
  if (groups === null) throw new Error("El servidor devolvió una lista de duplicados inválida");
  return groups;
}

export async function listCrossStateCandidates(): Promise<readonly CrossStateCandidateGroup[]> {
  const response = await fetch("/api/bff/games/cross-state-reconciliation", { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar la reconciliación");
  const candidates = normalizeCrossStateCandidates(await response.json());
  if (candidates === null) throw new Error("El servidor devolvió una reconciliación inválida");
  return candidates;
}

export async function mergeGame(absorbedGameId: number, intoGameId: number): Promise<MergeResult> {
  const response = await csrfFetch(`/api/bff/games/${absorbedGameId}/merge`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ intoGameId }), cache: "no-store" });
  if (response.status !== 200 && response.status !== 409) throw await parseApiError(response, "No se pudo fusionar el juego");
  const result = normalizeMergeResult(await response.json().catch(() => null));
  if (result === null) throw new Error("El servidor devolvió un resultado de fusión inválido");
  return result;
}
