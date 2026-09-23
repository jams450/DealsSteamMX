import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  normalizeManualAddResponse,
  normalizeManualSearchResponse,
  type ManualAddInput,
  type ManualAddResult,
  type ManualSearchResult
} from "@/lib/contracts/manual-library";

/**
 * Búsqueda de IGDB para el alta manual. `source === null` significa «proveedor no disponible», que el
 * diálogo debe distinguir de «sin resultados». Solo lectura: no escribe nada. Los candidatos del
 * catálogo no tienen endpoint aparte: llegan dentro del resultado del alta cuando el servidor se niega
 * a crear a ciegas.
 */
export async function searchManualGames(title: string): Promise<ManualSearchResult> {
  const response = await csrfFetch(`/api/bff/library/manual/enrich?title=${encodeURIComponent(title)}`, {
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo buscar en IGDB");

  const result = normalizeManualSearchResponse(await response.json());
  if (result === null) throw new Error("El servidor devolvió una búsqueda inválida");
  return result;
}

/**
 * Alta manual. El backend decide el outcome: `candidates` = el título ya existe y hay que elegir juego
 * (van en la respuesta), `duplicate` = la fila ya estaba. La portada la resuelve el servidor por
 * `igdbId`; esta función nunca envía una URL.
 */
export async function addManualGame(input: ManualAddInput): Promise<ManualAddResult> {
  const response = await csrfFetch("/api/bff/library/manual", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(input),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo añadir el juego");

  const result = normalizeManualAddResponse(await response.json());
  if (result === null) throw new Error("El servidor devolvió un resultado de alta inválido");
  return result;
}

/** Deshace un alta: borra la fila de la biblioteca (el juego canónico y sus datos quedan intactos). */
export async function deleteManualLibraryRow(userLibraryId: number): Promise<void> {
  const response = await csrfFetch(`/api/bff/library/manual/${userLibraryId}`, {
    method: "DELETE",
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo deshacer el alta");
}
