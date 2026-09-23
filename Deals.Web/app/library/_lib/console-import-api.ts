import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  CONSOLE_IMPORT_MAX_ENTRIES,
  normalizeConsoleImportCommit,
  normalizeConsoleImportPreview,
  toConsoleImportEntryPayload,
  type ConsoleImportCommitResult,
  type ConsoleImportDecision,
  type ConsoleImportEntryInput,
  type ConsoleImportPreview
} from "@/lib/contracts/console-import";

const PREVIEW_URL = "/api/bff/library/console-import/preview";
const COMMIT_URL = "/api/bff/library/console-import/commit";

/**
 * Preview de la importación de consolas: candidatos por título (nunca identidad) y plataformas
 * sugeridas. Solo lectura.
 *
 * El parser ya bloquea los archivos con más de `CONSOLE_IMPORT_MAX_ENTRIES` entradas importables, así
 * que en la práctica esto es una sola petición; el troceado queda como defensa por si el tope cambia:
 * es una lectura, repetirla no tiene efecto. Las filas de tienda nunca llegan aquí (el parser las
 * descarta antes), porque este contrato exige `Source` nulo.
 */
export async function previewConsoleImport(entries: readonly ConsoleImportEntryInput[]): Promise<ConsoleImportPreview> {
  const all: ConsoleImportPreview["entries"][number][] = [];

  for (let offset = 0; offset < entries.length; offset += CONSOLE_IMPORT_MAX_ENTRIES) {
    const chunk = entries.slice(offset, offset + CONSOLE_IMPORT_MAX_ENTRIES);
    const response = await csrfFetch(PREVIEW_URL, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(chunk.map(toConsoleImportEntryPayload)),
      cache: "no-store"
    });
    if (!response.ok) throw await parseApiError(response, "No se pudo previsualizar la importación de consolas");

    const preview = normalizeConsoleImportPreview(await response.json());
    if (preview === null) throw new Error("El servidor devolvió un preview de consolas inválido");
    all.push(...preview.entries);
  }

  return { entries: all };
}

/**
 * Commit de las decisiones explícitas. Un 409 **no es un error**: es un rechazo estructurado (nada se
 * escribió) que llega con la lista de conflictos de identidad, así que se devuelve igual que un commit
 * aplicado con `applied === false`. El resto de los no-2xx sí son errores con el sobre del BFF.
 */
export async function commitConsoleImport(
  decisions: readonly ConsoleImportDecision[]
): Promise<ConsoleImportCommitResult> {
  const response = await csrfFetch(COMMIT_URL, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(decisions),
    cache: "no-store"
  });

  if (response.status === 409) {
    const refusal = normalizeConsoleImportCommit(await response.json().catch(() => null));
    if (refusal === null) throw new Error("El servidor devolvió un conflicto de identidad inválido");
    return refusal;
  }

  if (!response.ok) throw await parseApiError(response, "No se pudo importar la biblioteca de consolas");

  const report = normalizeConsoleImportCommit(await response.json());
  if (report === null) throw new Error("El servidor devolvió un reporte de importación inválido");
  return report;
}
