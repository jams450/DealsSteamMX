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

export type JobOption = {
  readonly key: string;
  readonly label: string;
  readonly help: string;
  readonly defaultValue: boolean;
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
  readonly options: Record<string, boolean>;
  readonly confirm: boolean;
};

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
          const entry = record(option);
          const key = text(entry.key);
          if (key === null) return [];
          return [
            {
              key,
              label: text(entry.label) ?? key,
              help: text(entry.help) ?? "",
              defaultValue: entry.defaultValue === true
            }
          ];
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
 * valor del cliente para una clave desconocida se descarta, porque el backend la rechazaría y no hay nada
 * que el usuario pueda hacer con ese error.
 */
export function buildJobRunPayload(
  options: readonly JobOption[],
  values: Record<string, boolean | undefined>,
  confirm: boolean
): JobRunPayload {
  const payload: Record<string, boolean> = {};
  for (const option of options) {
    const selected = values[option.key];
    payload[option.key] = typeof selected === "boolean" ? selected : option.defaultValue;
  }

  return { options: payload, confirm };
}

/** Resumen legible de las opciones efectivas, para la tarjeta de confirmación. */
export function formatJobOptionValues(
  options: readonly JobOption[],
  values: Record<string, boolean | undefined>
): string[] {
  return options.map((option) => {
    const selected = values[option.key];
    const effective = typeof selected === "boolean" ? selected : option.defaultValue;
    return `${option.label}: ${effective ? "sí" : "no"}`;
  });
}

export function clampJobPage(page: number, totalPages: number): number {
  const ceiling = Math.max(1, Math.floor(count(totalPages, 1)));
  const requested = Math.floor(count(page, 1));
  if (!Number.isFinite(requested) || requested < 1) return 1;
  return Math.min(requested, ceiling);
}
