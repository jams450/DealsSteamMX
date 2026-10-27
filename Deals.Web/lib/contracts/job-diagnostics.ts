// Diagnóstico de sincronización por tienda y motivo (spec S5/S6/S7).
//
// Este módulo es la única puerta entre `job_runs.details` y la pestaña «Diagnóstico de sincronización».
// Tres invariantes:
//  1. el sobre se lee de `details.diagnostics` o de `details.result.diagnostics` (el runner envuelve el
//     resultado), tolerando camelCase y PascalCase. Una ejecución vieja, de otro esquema o corrupta se
//     reporta como «sin diagnóstico» o «no interpretable», NUNCA como «0 fallos»;
//  2. los contadores del `summary` son completos y no se recalculan desde `events`, que el colector acota a
//     200. El detalle se limita a lo que la ejecución guardó, y `totalEvents`/`shownEvents`/`truncatedEvents`
//     se corrigen para no mentir si el sobre trae menos eventos de los que declara;
//  3. solo se aceptan tokens de las taxonomías cerradas. Cualquier otro campo se descarta, y ningún mensaje,
//     pila, URL, cuerpo HTTP o credencial del proveedor puede llegar a la UI: el contrato no tiene dónde
//     ponerlos.

export const JOB_DIAGNOSTICS_SCHEMA_VERSION = 1;
/** Tope de eventos que el colector persiste por ejecución y que la UI muestra. */
export const MAX_DIAGNOSTIC_EVENTS = 200;
/**
 * Tope defensivo del lector. El backend agrega solo por (proveedor, resultado, motivo): 7 × 5 × 13 = 455
 * grupos como máximo, así que un reporte válido nunca lo alcanza. Más que esto deja de poder afirmarse que
 * el resumen es completo.
 */
export const MAX_DIAGNOSTIC_SUMMARY_GROUPS = 512;
export const MAX_DIAGNOSTIC_ERROR_TYPE_LENGTH = 64;
export const MAX_DIAGNOSTIC_ELAPSED_MS = 86_400_000;
export const MIN_DIAGNOSTIC_HTTP_STATUS = 100;
export const MAX_DIAGNOSTIC_HTTP_STATUS = 599;

export const DIAGNOSTIC_PROVIDERS = [
  "steam",
  "itad",
  "itad-identity",
  "itad-bundles",
  "ggdeals",
  "epic",
  "microsoft"
] as const;

export const DIAGNOSTIC_OUTCOMES = ["refreshed", "no_match", "failed", "skipped", "cancelled"] as const;

export const DIAGNOSTIC_REASONS = [
  "refreshed",
  "http_error",
  "transport_error",
  "timeout",
  "parse_error",
  "no_identity",
  "title_fallback_disabled",
  "store_no_offer",
  "not_comparable",
  "gate_skipped",
  "gate_declined",
  "cancelled",
  "unknown_failure"
] as const;

export type DiagnosticProvider = (typeof DIAGNOSTIC_PROVIDERS)[number];
export type DiagnosticOutcome = (typeof DIAGNOSTIC_OUTCOMES)[number];
export type DiagnosticReason = (typeof DIAGNOSTIC_REASONS)[number];

export type JobDiagnosticSummaryEntry = {
  readonly provider: DiagnosticProvider;
  readonly outcome: DiagnosticOutcome;
  readonly reason: DiagnosticReason;
  /**
   * Semántica de agregado: el backend suma todos los estados HTTP del grupo y deja este campo en `null`.
   * No significa «estado desconocido»; el estado real de cada evento está en `events`.
   */
  readonly httpStatus: number | null;
  readonly count: number;
};

export type JobDiagnosticEvent = {
  readonly appId: number | null;
  readonly gameId: number | null;
  readonly provider: DiagnosticProvider;
  readonly outcome: DiagnosticOutcome;
  readonly reason: DiagnosticReason;
  readonly httpStatus: number | null;
  readonly errorType: string | null;
  readonly elapsedMs: number | null;
};

export type JobDiagnostics = {
  readonly schemaVersion: typeof JOB_DIAGNOSTICS_SCHEMA_VERSION;
  readonly totalEvents: number;
  readonly shownEvents: number;
  readonly truncatedEvents: number;
  readonly summary: readonly JobDiagnosticSummaryEntry[];
  readonly events: readonly JobDiagnosticEvent[];
  /**
   * Solo de la UI: el sobre traía más grupos de los que este lector acepta. Los contadores mostrados no
   * pueden presentarse como completos (ver `diagnosticsCompletenessNote`).
   */
  readonly summaryTruncated: boolean;
};

/**
 * Resultado de leer `details`. Distinguir estos cuatro casos es lo que evita que una ejecución vieja se lea
 * como una sincronización sin fallos: `absent`/`unsupported`/`malformed` nunca producen contadores.
 */
export type JobDiagnosticsResult =
  | { readonly kind: "available"; readonly report: JobDiagnostics }
  | { readonly kind: "absent" }
  | { readonly kind: "unsupported"; readonly schemaVersion: number | null }
  | { readonly kind: "malformed" };

export type JobDiagnosticsFilters = {
  readonly provider: DiagnosticProvider | null;
  readonly reason: DiagnosticReason | null;
};

export const NO_DIAGNOSTIC_FILTERS: JobDiagnosticsFilters = { provider: null, reason: null };

function recordOf(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

/** El backend serializa en camelCase; se tolera PascalCase porque el contrato lo declara aceptable. */
function field(source: Record<string, unknown>, camel: string, pascal: string): unknown {
  if (Object.prototype.hasOwnProperty.call(source, camel)) return source[camel];
  if (Object.prototype.hasOwnProperty.call(source, pascal)) return source[pascal];
  return undefined;
}

function tokenOf<T extends string>(allowed: readonly T[], value: unknown): T | null {
  return typeof value === "string" && (allowed as readonly string[]).includes(value) ? (value as T) : null;
}

/** Un contador solo es un entero seguro no negativo; `"12"`, `1.5` y `-1` no se convierten. */
function countOf(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : null;
}

/** Identidad conocida: entero positivo. Un `0`, un negativo o un texto no se inventan como identidad. */
function idOf(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : null;
}

/** Estado HTTP ausente o inválido queda en `null`: no se inventa un 200 ni se pinta el texto crudo. */
function httpStatusOf(value: unknown): number | null {
  if (typeof value !== "number" || !Number.isInteger(value)) return null;
  return value >= MIN_DIAGNOSTIC_HTTP_STATUS && value <= MAX_DIAGNOSTIC_HTTP_STATUS ? value : null;
}

/** Duración en milisegundos: finita, no negativa y acotada; cualquier otra cosa es desconocida. */
function elapsedMsOf(value: unknown): number | null {
  if (typeof value !== "number" || !Number.isFinite(value)) return null;
  return value >= 0 && value <= MAX_DIAGNOSTIC_ELAPSED_MS ? value : null;
}

const ERROR_TYPE_IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_.]*$/;

/**
 * El tipo de excepción es un identificador acotado (`HttpRequestException`,
 * `System.Threading.Tasks.TaskCanceledException`), nunca un mensaje. Un valor que no lo parece se descarta:
 * la UI no pinta texto libre de una excepción.
 */
function errorTypeOf(value: unknown): string | null {
  if (typeof value !== "string" || value.length === 0 || value.length > MAX_DIAGNOSTIC_ERROR_TYPE_LENGTH) return null;
  return ERROR_TYPE_IDENTIFIER.test(value) ? value : null;
}

function normalizeSummaryEntry(raw: unknown): JobDiagnosticSummaryEntry | null {
  const value = recordOf(raw);
  if (!value) return null;

  const provider = tokenOf(DIAGNOSTIC_PROVIDERS, field(value, "provider", "Provider"));
  const outcome = tokenOf(DIAGNOSTIC_OUTCOMES, field(value, "outcome", "Outcome"));
  const reason = tokenOf(DIAGNOSTIC_REASONS, field(value, "reason", "Reason"));
  const count = countOf(field(value, "count", "Count"));
  if (!provider || !outcome || !reason || count === null) return null;

  return {
    provider,
    outcome,
    reason,
    httpStatus: httpStatusOf(field(value, "httpStatus", "HttpStatus")),
    count
  };
}

function normalizeEvent(raw: unknown): JobDiagnosticEvent | null {
  const value = recordOf(raw);
  if (!value) return null;

  const provider = tokenOf(DIAGNOSTIC_PROVIDERS, field(value, "provider", "Provider"));
  const outcome = tokenOf(DIAGNOSTIC_OUTCOMES, field(value, "outcome", "Outcome"));
  const reason = tokenOf(DIAGNOSTIC_REASONS, field(value, "reason", "Reason"));
  // Un evento sin taxonomía válida se descarta entero: no se inventa un proveedor ni un motivo.
  if (!provider || !outcome || !reason) return null;

  return {
    appId: idOf(field(value, "appId", "AppId")),
    gameId: idOf(field(value, "gameId", "GameId")),
    provider,
    outcome,
    reason,
    httpStatus: httpStatusOf(field(value, "httpStatus", "HttpStatus")),
    errorType: errorTypeOf(field(value, "errorType", "ErrorType")),
    elapsedMs: elapsedMsOf(field(value, "elapsedMs", "ElapsedMs"))
  };
}

/** `details.diagnostics` o `details.result.diagnostics`; `undefined`/`null` significa «no hay reporte». */
function diagnosticsField(details: Record<string, unknown>): unknown {
  const direct = field(details, "diagnostics", "Diagnostics");
  if (direct !== undefined) return direct;
  const result = recordOf(field(details, "result", "Result"));
  return result ? field(result, "diagnostics", "Diagnostics") : undefined;
}

/**
 * Lee y acota el diagnóstico de una ejecución. Devuelve `absent` cuando la ejecución no trae reporte
 * (vieja o de un job que no sincroniza), `unsupported` cuando el esquema no es el v1 congelado, y
 * `malformed` cuando el v1 no se puede validar completo. Ninguno de esos casos inventa contadores.
 */
export function normalizeJobDiagnostics(details: unknown): JobDiagnosticsResult {
  const payload = recordOf(details);
  if (!payload) return { kind: "absent" };

  const root = diagnosticsField(payload);
  if (root === undefined || root === null) return { kind: "absent" };

  const value = recordOf(root);
  if (!value) return { kind: "malformed" };

  const version = field(value, "schemaVersion", "SchemaVersion");
  if (typeof version !== "number" || !Number.isInteger(version)) return { kind: "malformed" };
  if (version !== JOB_DIAGNOSTICS_SCHEMA_VERSION) return { kind: "unsupported", schemaVersion: version };

  const rawSummary = field(value, "summary", "Summary");
  const rawEvents = field(value, "events", "Events");
  if (!Array.isArray(rawSummary) || !Array.isArray(rawEvents)) return { kind: "malformed" };

  const declaredTotal = countOf(field(value, "totalEvents", "TotalEvents"));
  const declaredShown = countOf(field(value, "shownEvents", "ShownEvents"));
  const declaredTruncated = countOf(field(value, "truncatedEvents", "TruncatedEvents"));
  if (declaredTotal === null || declaredShown === null || declaredTruncated === null) {
    return { kind: "malformed" };
  }

  // Un grupo inválido invalida el reporte: el resumen se declara completo, así que no se puede descartar
  // una fila en silencio y seguir presentando los totales como completos.
  const summary: JobDiagnosticSummaryEntry[] = [];
  const summaryLimit = Math.min(rawSummary.length, MAX_DIAGNOSTIC_SUMMARY_GROUPS);
  for (let index = 0; index < summaryLimit; index += 1) {
    const entry = normalizeSummaryEntry(rawSummary[index]);
    if (!entry) return { kind: "malformed" };
    summary.push(entry);
  }

  // El detalle es una muestra acotada: un evento ilegible se descarta, y los contadores se ajustan para no
  // afirmar que se muestran más eventos de los que hay.
  const events: JobDiagnosticEvent[] = [];
  const eventLimit = Math.min(rawEvents.length, MAX_DIAGNOSTIC_EVENTS);
  for (let index = 0; index < eventLimit; index += 1) {
    const event = normalizeEvent(rawEvents[index]);
    if (event) events.push(event);
  }

  const shownEvents = events.length;
  const totalEvents = Math.max(declaredTotal, shownEvents);
  // `shown + truncated = total` siempre: un sobre que declara más truncados que eventos no se cree.
  const truncatedEvents = Math.max(0, totalEvents - shownEvents);

  return {
    kind: "available",
    report: {
      schemaVersion: JOB_DIAGNOSTICS_SCHEMA_VERSION,
      totalEvents,
      shownEvents,
      truncatedEvents,
      summary,
      events,
      summaryTruncated: rawSummary.length > MAX_DIAGNOSTIC_SUMMARY_GROUPS
    }
  };
}

const PROVIDER_LABELS: Record<DiagnosticProvider, string> = {
  steam: "Steam",
  itad: "IsThereAnyDeal",
  "itad-identity": "ITAD identidad",
  "itad-bundles": "ITAD bundles",
  ggdeals: "GG.deals",
  epic: "Epic Games Store",
  microsoft: "Microsoft Store"
};

const OUTCOME_LABELS: Record<DiagnosticOutcome, string> = {
  refreshed: "Actualizado",
  no_match: "Sin coincidencia",
  failed: "Fallido",
  skipped: "Omitido",
  cancelled: "Cancelado"
};

const REASON_LABELS: Record<DiagnosticReason, string> = {
  refreshed: "Actualizado",
  http_error: "Error HTTP",
  transport_error: "Error de transporte",
  timeout: "Tiempo agotado",
  parse_error: "Error de lectura",
  no_identity: "Sin identidad",
  title_fallback_disabled: "Búsqueda por título deshabilitada",
  store_no_offer: "La tienda no lo vende",
  not_comparable: "No comparable",
  gate_skipped: "Omitido por frescura",
  gate_declined: "Rechazado por frescura",
  cancelled: "Cancelado",
  unknown_failure: "Fallo desconocido"
};

export function diagnosticProviderLabel(provider: DiagnosticProvider): string {
  return PROVIDER_LABELS[provider] ?? provider;
}

export function diagnosticOutcomeLabel(outcome: DiagnosticOutcome): string {
  return OUTCOME_LABELS[outcome] ?? outcome;
}

export function diagnosticReasonLabel(reason: DiagnosticReason): string {
  return REASON_LABELS[reason] ?? reason;
}

/** Mensaje honesto para una ejecución sin diagnóstico legible. Nunca dice «0 fallos». */
export function diagnosticsAvailabilityMessage(result: Exclude<JobDiagnosticsResult, { kind: "available" }>): string {
  if (result.kind === "absent") {
    return "Esta ejecución no guardó un diagnóstico de sincronización. No significa que haya fallado ni que no haya fallos: el reporte no existe para esta ejecución.";
  }
  if (result.kind === "unsupported") {
    return `El diagnóstico de esta ejecución usa el esquema ${result.schemaVersion ?? "desconocido"} y esta versión no lo interpreta. No se contabiliza como 0 fallos.`;
  }
  return "El diagnóstico de esta ejecución llegó incompleto o con valores fuera del contrato. No se contabiliza como 0 fallos.";
}

/** Aviso cuando el detalle es una muestra: el resumen sigue siendo completo. */
export function diagnosticsTruncationMessage(report: JobDiagnostics): string | null {
  if (report.truncatedEvents <= 0) return null;
  return `El detalle es una muestra: se guardan ${report.shownEvents} eventos y ${report.truncatedEvents} no se muestran (${report.totalEvents} en total). Los contadores del resumen sí son completos.`;
}

/** Aviso cuando el propio resumen superó el tope del lector: ya no se puede afirmar que sea completo. */
export function diagnosticsCompletenessNote(report: JobDiagnostics): string | null {
  return report.summaryTruncated
    ? `El resumen trae más de ${MAX_DIAGNOSTIC_SUMMARY_GROUPS} grupos y se cortó al leerlo: los totales mostrados no son completos.`
    : null;
}

/** Opciones de filtro tomadas del resumen completo, no del detalle acotado. */
export function diagnosticFilterOptions(report: JobDiagnostics): {
  readonly providers: readonly DiagnosticProvider[];
  readonly reasons: readonly DiagnosticReason[];
} {
  const providersPresent = new Set(report.summary.map((entry) => entry.provider));
  const reasonsPresent = new Set(report.summary.map((entry) => entry.reason));
  return {
    providers: DIAGNOSTIC_PROVIDERS.filter((provider) => providersPresent.has(provider)),
    reasons: DIAGNOSTIC_REASONS.filter((reason) => reasonsPresent.has(reason))
  };
}

export function parseDiagnosticProviderFilter(value: string): DiagnosticProvider | null {
  return tokenOf(DIAGNOSTIC_PROVIDERS, value);
}

export function parseDiagnosticReasonFilter(value: string): DiagnosticReason | null {
  return tokenOf(DIAGNOSTIC_REASONS, value);
}

export function filterDiagnosticSummary(
  report: JobDiagnostics,
  filters: JobDiagnosticsFilters
): readonly JobDiagnosticSummaryEntry[] {
  return report.summary.filter(
    (entry) =>
      (filters.provider === null || entry.provider === filters.provider) &&
      (filters.reason === null || entry.reason === filters.reason)
  );
}

export function filterDiagnosticEvents(
  report: JobDiagnostics,
  filters: JobDiagnosticsFilters
): readonly JobDiagnosticEvent[] {
  return report.events.filter(
    (event) =>
      (filters.provider === null || event.provider === filters.provider) &&
      (filters.reason === null || event.reason === filters.reason)
  );
}

/** Suma de los contadores del resumen filtrado; sale del resumen completo, no de `events`. */
export function diagnosticSummaryCount(entries: readonly JobDiagnosticSummaryEntry[]): number {
  return entries.reduce((total, entry) => total + entry.count, 0);
}

/**
 * Aviso del detalle filtrado. Con eventos truncados, el número de filas visibles no puede presentarse como
 * si fuera el total del filtro: se dicen los dos números.
 */
export function diagnosticFilteredDetailNote(
  report: JobDiagnostics,
  filteredSummaryCount: number,
  filteredEventCount: number
): string | null {
  if (report.truncatedEvents === 0) return null;
  return `El detalle filtrado es parcial: el resumen completo de esta selección suma ${filteredSummaryCount} eventos y aquí se ven ${filteredEventCount}. Los contadores de arriba no se recalculan desde el detalle.`;
}

/**
 * Aclaración del alcance del resumen. El backend agrupa únicamente por (proveedor, resultado, motivo) —455
 * grupos como máximo— y suma todos los estados HTTP de cada grupo; el campo `httpStatus` del resumen llega
 * `null` por diseño. Leerlo como «no se conoce el estado» sería falso: los estados reales están en el
 * detalle por juego, y la UI no pinta una columna HTTP en el resumen.
 */
export function diagnosticSummaryScopeNote(): string {
  return "El resumen suma todos los estados HTTP de cada combinación de proveedor, resultado y motivo. El estado real de cada evento aparece en el detalle por juego.";
}

export function diagnosticHttpStatusText(httpStatus: number | null): string {
  return httpStatus === null ? "—" : String(httpStatus);
}

export function diagnosticElapsedText(elapsedMs: number | null): string {
  if (elapsedMs === null) return "—";
  if (elapsedMs < 1_000) return `${Math.round(elapsedMs)} ms`;
  return `${(elapsedMs / 1_000).toFixed(1)} s`;
}

/**
 * Enlace al detalle del juego solo cuando la AppID de Steam es conocida. Un `gameId` canónico no se traduce
 * a `/games/<appId>` ni a una URL inventada: sin identidad conocida, la celda muestra el id, no un enlace.
 */
export function diagnosticGameHref(event: JobDiagnosticEvent): string | null {
  return event.appId === null ? null : `/games/${event.appId}`;
}

/**
 * Guarda de selección para el detalle: numera cada pedido y descarta el que ya no es el vigente. Un cambio
 * rápido de ejecución no puede dejar ganar a la respuesta anterior.
 */
export class DiagnosticSelectionGuard {
  #current = 0;

  begin(): number {
    this.#current += 1;
    return this.#current;
  }

  invalidate(): void {
    this.#current += 1;
  }

  isCurrent(token: number): boolean {
    return token === this.#current;
  }
}
