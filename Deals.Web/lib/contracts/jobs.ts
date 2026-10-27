// Contrato del panel de Jobs (`GET /api/jobs`, `GET /api/jobs/catalog`, `GET /api/jobs/{id}` y
// `POST /api/jobs/{job}/run`).
//
// Tres invariantes que la UI no puede romper:
//  1. una ejecución sin `jobRunId` numérico no existe: se descarta en vez de renderizar una fila vacía;
//  2. el catálogo lista también los jobs deshabilitados, con su motivo; nunca se inventa una opción que el
//     backend no declaró, y el payload solo envía opciones declaradas;
//  3. la paginación se limita al rango que el servidor ya contestó.

export const JOB_RUNS_PAGE_SIZE = 20;

export type JobRun = {
  readonly jobRunId: number;
  readonly job: string;
  readonly trigger: string;
  readonly status: string;
  readonly startedAt: string;
  readonly finishedAt: string | null;
  readonly durationMilliseconds: number | null;
  readonly details: unknown;
};

export type JobRunFilters = {
  readonly job: string | null;
  readonly status: string | null;
  readonly trigger: string | null;
};

export type JobsResponse = {
  readonly summary: {
    readonly total: number;
    readonly running: number;
    readonly ok: number;
    readonly failed: number;
    readonly lastStartedAt: string | null;
  };
  readonly items: readonly JobRun[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalPages: number;
  readonly filters: JobRunFilters;
};

/**
 * Tipo de una opción declarada por el backend. `integer` es un entero escrito por el usuario (horas);
 * `boolean` es la casilla de siempre. Un descriptor sin `type` se lee como `boolean`, que es la forma
 * que ya tenían todos los jobs antes de existir las opciones numéricas.
 */
export type JobOptionType = "boolean" | "integer";

export type JobOptionValue = boolean | number;

/** Lo que el usuario tiene en la UI: la casilla (booleano) o el texto tal cual del campo numérico. */
export type JobOptionInput = JobOptionValue | string;

export type JobOption = {
  readonly key: string;
  readonly label: string;
  readonly help: string;
  readonly type: JobOptionType;
  /**
   * Default efectivo. Es `null` solo si el backend declaró `integer` con un default que no es un entero
   * dentro de sus propios límites: se rechaza en vez de redondearlo o leerlo como si fuera válido.
   */
  readonly defaultValue: boolean | number | null;
  readonly minValue: number | null;
  readonly maxValue: number | null;
};

export type JobCatalogItem = {
  readonly job: string;
  readonly displayName: string;
  readonly description: string;
  readonly enabled: boolean;
  readonly disabledReason: string | null;
  readonly schedule: string;
  readonly running: boolean;
  /** La última fila quedó en `running` más allá de la ventana: el proceso murió, el job es lanzable. */
  readonly stale: boolean;
  readonly destructive: boolean;
  readonly requiresConfirmation: boolean;
  readonly options: readonly JobOption[];
  readonly lastRun: JobRun | null;
};

export type JobCatalog = {
  readonly items: readonly JobCatalogItem[];
  readonly generatedAt: string | null;
};

export type JobRunPayload = {
  readonly options: Record<string, JobOptionValue>;
  readonly confirm: boolean;
};

/** Lo lanza el payload builder cuando un valor declarado no se puede enviar: nunca se cae al default. */
export class JobOptionValidationError extends Error {
  readonly optionKey: string;

  constructor(option: JobOption, reason: string) {
    super(`${option.label}: ${reason}`);
    this.name = "JobOptionValidationError";
    this.optionKey = option.key;
  }
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

function text(value: unknown): string | null {
  return typeof value === "string" && value.trim().length > 0 ? value : null;
}

function count(value: unknown, fallback: number): number {
  // `Number(null)` y `Number("")` son 0: un campo ausente no puede convertirse en una duración de 0 ms.
  if (value === null || value === undefined || value === "") return fallback;
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function positiveInt(value: unknown): number | null {
  const parsed = count(value, Number.NaN);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

/** Un límite solo cuenta si ya es un número entero; `"1"` o `720.5` no se convierten. */
function integerBound(value: unknown): number | null {
  return typeof value === "number" && Number.isInteger(value) ? value : null;
}

function withinBounds(value: number, minValue: number | null, maxValue: number | null): boolean {
  return (minValue === null || value >= minValue) && (maxValue === null || value <= maxValue);
}

function boundsReason(minValue: number | null, maxValue: number | null): string {
  if (minValue !== null && maxValue !== null) return `Debe estar entre ${minValue} y ${maxValue}.`;
  if (minValue !== null) return `Debe ser al menos ${minValue}.`;
  return `Debe ser como máximo ${maxValue}.`;
}

/**
 * Lee un descriptor del backend. Un `integer` cuyo default o límites no son enteros no se coerciona: el
 * default queda en `null` (el campo se pinta vacío) y los límites ilegibles se ignoran, porque la API
 * sigue siendo la que valida. Un `type` ausente o desconocido es un booleano, como antes.
 */
function normalizeJobOption(input: unknown): JobOption | null {
  const entry = record(input);
  const key = text(entry.key);
  if (key === null) return null;

  const type: JobOptionType = entry.type === "integer" ? "integer" : "boolean";
  const label = text(entry.label) ?? key;
  const help = text(entry.help) ?? "";

  if (type === "boolean") {
    return { key, label, help, type, defaultValue: entry.defaultValue === true, minValue: null, maxValue: null };
  }

  const minValue = integerBound(entry.minValue);
  const maxValue = integerBound(entry.maxValue);
  const declaredDefault = integerBound(entry.defaultValue);
  const defaultValue =
    declaredDefault !== null && withinBounds(declaredDefault, minValue, maxValue) ? declaredDefault : null;

  return { key, label, help, type, defaultValue, minValue, maxValue };
}

type JobOptionParseResult =
  | { readonly ok: true; readonly value: JobOptionValue }
  | { readonly ok: false; readonly error: string };

const INTEGER_TEXT = /^-?\d+$/;

/**
 * Valida y normaliza un valor declarado. `present` distingue «la clave no existe» (se usa el default, que es
 * el comportamiento histórico de las casillas) de «el usuario borró el campo» (error: no se envía el default
 * en silencio). Un entero nunca se redondea y nunca se lee de `"72.5"`, `"1e2"` o `true`.
 */
function parseJobOption(option: JobOption, raw: JobOptionInput | undefined, present: boolean): JobOptionParseResult {
  if (option.type !== "integer") {
    if (!present) return { ok: true, value: option.defaultValue === true };
    return typeof raw === "boolean"
      ? { ok: true, value: raw }
      : { ok: false, error: "Marca o desmarca la opción." };
  }

  if (!present) {
    return typeof option.defaultValue === "number"
      ? { ok: true, value: option.defaultValue }
      : { ok: false, error: "Escribe un número entero." };
  }

  let parsed: number;
  if (typeof raw === "number") {
    if (!Number.isInteger(raw)) return { ok: false, error: "Debe ser un número entero, sin decimales." };
    parsed = raw;
  } else if (typeof raw === "string") {
    const trimmed = raw.trim();
    if (trimmed.length === 0) return { ok: false, error: "Escribe un número entero; el campo está vacío." };
    if (!INTEGER_TEXT.test(trimmed)) return { ok: false, error: "Debe ser un número entero, sin decimales ni otros caracteres." };
    parsed = Number(trimmed);
    if (!Number.isInteger(parsed)) return { ok: false, error: "Debe ser un número entero, sin decimales." };
  } else {
    return { ok: false, error: "Escribe un número entero." };
  }

  return withinBounds(parsed, option.minValue, option.maxValue)
    ? { ok: true, value: parsed }
    : { ok: false, error: boundsReason(option.minValue, option.maxValue) };
}

export function normalizeJobRun(input: unknown): JobRun | null {
  const value = record(input);
  const jobRunId = positiveInt(value.jobRunId);
  const job = text(value.job);
  const startedAt = text(value.startedAt);
  if (jobRunId === null || job === null || startedAt === null) {
    return null;
  }

  const duration = count(value.durationMilliseconds, Number.NaN);

  return {
    jobRunId,
    job,
    trigger: text(value.trigger) ?? "unknown",
    status: text(value.status) ?? "unknown",
    startedAt,
    finishedAt: text(value.finishedAt),
    durationMilliseconds: Number.isFinite(duration) ? duration : null,
    details: value.details ?? null
  };
}

export function normalizeJobCatalog(input: unknown): JobCatalog {
  const value = record(input);
  const rawItems = Array.isArray(value.items) ? value.items : [];
  const items: JobCatalogItem[] = [];

  for (const raw of rawItems) {
    const item = record(raw);
    const job = text(item.job);
    if (job === null) continue;

    const options = Array.isArray(item.options)
      ? item.options.flatMap((option): JobOption[] => {
          const normalized = normalizeJobOption(option);
          return normalized ? [normalized] : [];
        })
      : [];

    items.push({
      job,
      displayName: text(item.displayName) ?? job,
      description: text(item.description) ?? "",
      enabled: item.enabled === true,
      disabledReason: text(item.disabledReason),
      schedule: text(item.schedule) ?? "",
      running: item.running === true,
      stale: item.stale === true,
      destructive: item.destructive === true,
      requiresConfirmation: item.requiresConfirmation === true,
      options,
      lastRun: normalizeJobRun(item.lastRun)
    });
  }

  return { items, generatedAt: text(value.generatedAt) };
}

export function normalizeJobs(input: unknown): JobsResponse {
  const value = record(input);
  const summaryValue = record(value.summary);
  const filters = record(value.filters);
  const items = Array.isArray(value.items)
    ? value.items.flatMap((item): JobRun[] => {
        const run = normalizeJobRun(item);
        return run ? [run] : [];
      })
    : [];

  return {
    summary: {
      total: count(summaryValue.total, 0),
      running: count(summaryValue.running, 0),
      ok: count(summaryValue.ok, 0),
      failed: count(summaryValue.failed, 0),
      lastStartedAt: text(summaryValue.lastStartedAt)
    },
    items,
    page: positiveInt(value.page) ?? 1,
    pageSize: positiveInt(value.pageSize) ?? 25,
    totalPages: positiveInt(value.totalPages) ?? 1,
    filters: { job: text(filters.job), status: text(filters.status), trigger: text(filters.trigger) }
  };
}

/**
 * Construye el cuerpo de `POST /api/jobs/{job}/run`. Solo viajan las opciones que el catálogo declaró: un
 * valor del cliente para una clave desconocida —`userId` incluido— se descarta, porque el backend la
 * rechazaría y no hay nada que el usuario pueda hacer con ese error. Un valor declarado que no se puede
 * enviar lanza `JobOptionValidationError`: no se sustituye por el default ni se redondea.
 */
export function buildJobRunPayload(
  options: readonly JobOption[],
  values: Record<string, JobOptionInput | undefined>,
  confirm: boolean
): JobRunPayload {
  const payload: Record<string, JobOptionValue> = {};
  for (const option of options) {
    const present = Object.prototype.hasOwnProperty.call(values, option.key);
    const result = parseJobOption(option, values[option.key], present);
    if (!result.ok) throw new JobOptionValidationError(option, result.error);
    payload[option.key] = result.value;
  }

  return { options: payload, confirm };
}

/** Motivo por el que un valor no se puede enviar, o `null` si es válido. */
export function jobOptionValueError(option: JobOption, raw: JobOptionInput | undefined): string | null {
  const result = parseJobOption(option, raw, raw !== undefined);
  return result.ok ? null : result.error;
}

/**
 * Texto que pinta el campo numérico: el texto del usuario tal cual (para no redondear ni recortar mientras
 * escribe), o el default declarado si es válido. Nunca inventa un `72` que el backend no declaró.
 */
export function jobOptionInputText(option: JobOption, raw: JobOptionInput | undefined): string {
  if (typeof raw === "string") return raw;
  if (typeof raw === "number") return String(raw);
  return typeof option.defaultValue === "number" ? String(option.defaultValue) : "";
}

/** Descripción accesible del rango declarado; `null` cuando el descriptor no limita la opción. */
export function jobOptionRangeText(option: JobOption): string | null {
  if (option.type !== "integer") return null;
  const { minValue, maxValue } = option;
  if (minValue !== null && maxValue !== null) return `Rango permitido: ${minValue}–${maxValue}.`;
  if (minValue !== null) return `Mínimo permitido: ${minValue}.`;
  if (maxValue !== null) return `Máximo permitido: ${maxValue}.`;
  return null;
}

function formatJobOptionValue(option: JobOption, value: JobOptionValue): string {
  return option.type === "integer" ? String(value) : value === true ? "sí" : "no";
}

/** Resumen legible de las opciones efectivas, para la tarjeta de confirmación. */
export function formatJobOptionValues(
  options: readonly JobOption[],
  values: Record<string, JobOptionInput | undefined>
): string[] {
  return options.map((option) => {
    const present = Object.prototype.hasOwnProperty.call(values, option.key);
    const result = parseJobOption(option, values[option.key], present);
    return `${option.label}: ${result.ok ? formatJobOptionValue(option, result.value) : "—"}`;
  });
}

export function clampJobPage(page: number, totalPages: number): number {
  const ceiling = Math.max(1, Math.floor(count(totalPages, 1)));
  const requested = Math.floor(count(page, 1));
  if (!Number.isFinite(requested) || requested < 1) return 1;
  return Math.min(requested, ceiling);
}
