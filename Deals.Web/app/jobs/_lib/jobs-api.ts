import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  normalizeJobCatalog,
  normalizeJobRun,
  normalizeJobs,
  type JobCatalog,
  type JobRun,
  type JobRunPayload,
  type JobsResponse
} from "@/lib/contracts/jobs";

export type JobsQuery = {
  readonly page: number;
  readonly pageSize?: number;
  readonly job?: string | null;
  readonly status?: string | null;
  readonly trigger?: string | null;
};

export type JobRunQueued = {
  readonly jobRunId: number;
  readonly job: string;
  readonly status: string;
  readonly requestedAt: string | null;
};

function toQuery(query: JobsQuery): string {
  const params = new URLSearchParams({
    page: String(query.page),
    pageSize: String(query.pageSize ?? 20)
  });
  if (query.job) params.set("job", query.job);
  if (query.status) params.set("status", query.status);
  if (query.trigger) params.set("trigger", query.trigger);
  return params.toString();
}

export async function getJobCatalog(): Promise<JobCatalog> {
  const response = await fetch("/api/bff/jobs/catalog", { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar el catálogo de jobs");
  return normalizeJobCatalog(await response.json());
}

export async function getJobRuns(query: JobsQuery): Promise<JobsResponse> {
  const response = await fetch(`/api/bff/jobs?${toQuery(query)}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar el historial de jobs");
  return normalizeJobs(await response.json());
}

export async function getJobRun(jobRunId: number): Promise<JobRun> {
  const response = await fetch(`/api/bff/jobs/${jobRunId}`, { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar la ejecución");
  const run = normalizeJobRun(await response.json());
  if (!run) throw new Error("La ejecución llegó incompleta");
  return run;
}

/**
 * Pide una ejecución manual. La API responde 202 con el id: el trabajo corre en el host, así que aquí solo
 * se devuelve el identificador que la página seguirá.
 */
export async function runJob(job: string, payload: JobRunPayload): Promise<JobRunQueued> {
  const response = await csrfFetch(`/api/bff/jobs/${encodeURIComponent(job)}/run`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo iniciar la ejecución");

  const body = (await response.json().catch(() => null)) as Record<string, unknown> | null;
  const jobRunId = Number(body?.jobRunId);
  if (!Number.isInteger(jobRunId) || jobRunId <= 0) {
    throw new Error("La ejecución se aceptó sin identificador; revisa el historial");
  }

  return {
    jobRunId,
    job: typeof body?.job === "string" ? body.job : job,
    status: typeof body?.status === "string" ? body.status : "running",
    requestedAt: typeof body?.requestedAt === "string" ? body.requestedAt : null
  };
}
