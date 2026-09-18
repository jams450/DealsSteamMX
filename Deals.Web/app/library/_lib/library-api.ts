import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
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
