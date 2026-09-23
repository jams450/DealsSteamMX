import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  normalizeCoverSyncReport,
  normalizeCoverUrl,
  type CoverPick,
  type LibraryCoverSyncReport
} from "@/lib/contracts/library-covers";
import {
  normalizeLibraryImportResponse,
  normalizeLibraryResponse,
  type LibraryImportReport,
  type LibraryResponse
} from "./library-contract";

const INVALID_LIBRARY = "El servidor devolvió una biblioteca inválida";

export async function getLibrary(): Promise<LibraryResponse> {
  const response = await fetch("/api/bff/library", { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar la biblioteca");

  const library = normalizeLibraryResponse(await response.json());
  if (library === null) throw new Error(INVALID_LIBRARY);
  return library;
}

// `entries` es el arreglo ya parseado del export de Playnite. Se reenvía tal cual: el backend valida
// campo por campo y descarta el archivo; el navegador nunca manda nada más que el JSON elegido.
export async function importLibrary(entries: readonly unknown[]): Promise<LibraryImportReport> {
  const response = await csrfFetch("/api/bff/library/import", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(entries),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo importar la biblioteca");

  const report = normalizeLibraryImportResponse(await response.json());
  if (report === null) throw new Error("El servidor devolvió un reporte de importación inválido");
  return report;
}

/**
 * Rellena las portadas que faltan desde Steam. Solo escribe la URL en el juego canónico: no reclama
 * identidad, no toca precios y nunca reemplaza una portada que ya exista. La pasada es acotada, así que
 * repetirla avanza; el reporte dice cuánto queda.
 */
export async function syncLibraryCovers(limit?: number): Promise<LibraryCoverSyncReport> {
  const response = await csrfFetch("/api/bff/library/covers/sync", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(limit === undefined ? {} : { limit }),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudieron sincronizar las portadas");

  const report = normalizeCoverSyncReport(await response.json());
  if (report === null) throw new Error("El servidor devolvió un reporte de portadas inválido");
  return report;
}

/**
 * Coloca la portada de un juego canónico desde el id elegido en la búsqueda: el appid de Steam o el de
 * IGDB, exactamente uno de los dos (`CoverPick`) y nunca una URL — la resuelve el servidor. Devuelve la
 * URL que guardó el servidor, que es la que la grilla debe pintar.
 */
export async function setGameCover(gameId: number, pick: CoverPick): Promise<string> {
  const response = await csrfFetch(`/api/bff/games/${gameId}/cover`, {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(pick),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo guardar la portada");

  const imageUrl = normalizeCoverUrl(await response.json());
  if (imageUrl === null) throw new Error("El servidor no devolvió la portada guardada");
  return imageUrl;
}
