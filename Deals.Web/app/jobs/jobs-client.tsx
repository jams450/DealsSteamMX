"use client";

import { ChevronLeft, ChevronRight, Play, RefreshCw } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AdminShell } from "@/components/navigation/admin-shell";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Select } from "@/components/ui/select";
import {
  buildJobRunPayload,
  clampJobPage,
  formatJobOptionValues,
  JOB_RUNS_PAGE_SIZE,
  type JobCatalog,
  type JobCatalogItem,
  type JobRun,
  type JobRunPayload,
  type JobsResponse
} from "@/lib/contracts/jobs";
import { getJobCatalog, getJobRun, getJobRuns, runJob, type JobRunQueued } from "./_lib/jobs-api";

// Copy de estados: el backend devuelve claves de máquina (`running`, `ok`, `failed`, `scheduled`, `startup`,
// `manual`) y la traducción vive aquí, no en el contrato.
const STATUS_LABELS: Record<string, string> = {
  running: "En ejecución",
  ok: "Correcto",
  failed: "Fallido"
};

const TRIGGER_LABELS: Record<string, string> = {
  scheduled: "Programado",
  startup: "Arranque",
  manual: "Manual"
};

const STATUS_BADGE: Record<string, string> = {
  running: "tabler-badge-info",
  ok: "tabler-badge-success",
  failed: "tabler-badge-danger"
};

const dateTimeFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "short", timeStyle: "medium" });

function statusLabel(status: string) {
  return STATUS_LABELS[status] ?? status;
}

function triggerLabel(trigger: string) {
  return TRIGGER_LABELS[trigger] ?? trigger;
}

function formatDateTime(value: string | null) {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : dateTimeFormatter.format(date);
}

function formatDuration(milliseconds: number | null) {
  if (milliseconds === null) return "—";
  if (milliseconds < 1_000) return `${Math.round(milliseconds)} ms`;
  return `${(milliseconds / 1_000).toFixed(1)} s`;
}

// `details` puede traer un error del proveedor o simplemente contadores. Se muestra entero y sin recortar:
// esta página existe para leer el detalle, no para esconderlo detrás de una celda truncada.
function detailsText(details: unknown) {
  if (details === null || details === undefined) return null;
  try {
    return JSON.stringify(details, null, 2);
  } catch {
    return String(details);
  }
}

function errorMessageOf(details: unknown) {
  if (!details || typeof details !== "object") return null;
  const record = details as Record<string, unknown>;
  const direct = record.error;
  const nested = (record.result as Record<string, unknown> | undefined)?.error;
  const reason = record.reason;
  const message = typeof direct === "string" ? direct : typeof nested === "string" ? nested : typeof reason === "string" ? reason : null;
  const type = typeof record.errorType === "string" ? record.errorType : null;
  return message ? (type ? `${type}: ${message}` : message) : null;
}

function StatusBadge({ status }: { readonly status: string }) {
  return <span className={`tabler-badge ${STATUS_BADGE[status] ?? "tabler-badge-muted"}`}>{statusLabel(status)}</span>;
}

function DetailsBlock({ run }: { readonly run: JobRun }) {
  const text = detailsText(run.details);
  const error = errorMessageOf(run.details);

  if (!text) {
    return <p className="text-sm text-muted">Esta ejecución no registró detalle.</p>;
  }

  return (
    <div className="space-y-2">
      {error ? <Alert variant="danger">{error}</Alert> : null}
      <pre className="max-h-80 overflow-auto rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-3)] p-3 text-xs text-secondary">
        {text}
      </pre>
    </div>
  );
}

function CatalogCard({
  item,
  onSelect,
  onShowLast
}: {
  readonly item: JobCatalogItem;
  readonly onSelect: (job: string) => void;
  readonly onShowLast: (run: JobRun) => void;
}) {
  return (
    <article className="space-y-2 rounded-[var(--radius-md)] border border-default p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-sm font-semibold text-primary">{item.displayName}</h3>
        <div className="flex flex-wrap items-center gap-1.5">
          {item.enabled ? (
            <span className="tabler-badge tabler-badge-muted">Habilitado</span>
          ) : (
            <span className="tabler-badge tabler-badge-warning">Deshabilitado</span>
          )}
          {item.running ? <span className="tabler-badge tabler-badge-info">En ejecución</span> : null}
          {item.stale ? <span className="tabler-badge tabler-badge-warning">Ejecución interrumpida</span> : null}
          {item.destructive ? <span className="tabler-badge tabler-badge-danger">Borra datos</span> : null}
        </div>
      </div>

      <p className="text-xs text-secondary">{item.description}</p>
      <p className="text-xs text-muted">Programación: {item.schedule}</p>
      {item.disabledReason ? <p className="text-xs text-muted">{item.disabledReason}</p> : null}

      <div className="flex flex-wrap items-center justify-between gap-2 border-t border-default pt-2 text-xs">
        {item.lastRun ? (
          <span className="flex flex-wrap items-center gap-1.5 text-muted">
            <span>Última:</span>
            <StatusBadge status={item.lastRun.status} />
            <span>{triggerLabel(item.lastRun.trigger)}</span>
            <span>{formatDateTime(item.lastRun.startedAt)}</span>
          </span>
        ) : (
          <span className="text-muted">Sin ejecuciones registradas.</span>
        )}
        <span className="flex gap-1.5">
          {item.lastRun ? (
            <Button type="button" variant="ghost" className="h-8 px-2" onClick={() => onShowLast(item.lastRun!)}>
              Ver último
            </Button>
          ) : null}
          <Button type="button" variant="secondary" className="h-8 px-2" onClick={() => onSelect(item.job)}>
            Seleccionar
          </Button>
        </span>
      </div>
    </article>
  );
}

type Feedback =
  | { readonly kind: "idle" }
  | { readonly kind: "queued"; readonly queued: JobRunQueued }
  | { readonly kind: "success"; readonly message: string; readonly run: JobRun | null }
  | { readonly kind: "error"; readonly message: string };

export function JobsClient({ username }: { username: string }) {
  const [catalog, setCatalog] = useState<JobCatalog | null>(null);
  const [catalogError, setCatalogError] = useState<string | null>(null);
  const [selectedJob, setSelectedJob] = useState<string>("");
  const [optionValues, setOptionValues] = useState<Record<string, boolean>>({});
  const [confirmDestructive, setConfirmDestructive] = useState(false);
  const [starting, setStarting] = useState(false);
  const [feedback, setFeedback] = useState<Feedback>({ kind: "idle" });
  const [watchRunId, setWatchRunId] = useState<number | null>(null);
  const [watched, setWatched] = useState<JobRun | null>(null);

  const [history, setHistory] = useState<JobsResponse | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [historyLoading, setHistoryLoading] = useState(true);
  const [filterJob, setFilterJob] = useState("");
  const [filterStatus, setFilterStatus] = useState("");
  const [filterTrigger, setFilterTrigger] = useState("");
  const [expandedRunId, setExpandedRunId] = useState<number | null>(null);
  const historyRequest = useRef(0);

  const loadCatalog = useCallback(async () => {
    try {
      const result = await getJobCatalog();
      setCatalog(result);
      setCatalogError(null);
      return result;
    } catch (cause) {
      setCatalogError(cause instanceof Error ? cause.message : "No se pudo cargar el catálogo de jobs");
      return null;
    }
  }, []);

  const loadHistory = useCallback(
    async (targetPage: number) => {
      const request = ++historyRequest.current;
      setHistoryLoading(true);
      try {
        const result = await getJobRuns({
          page: targetPage,
          pageSize: JOB_RUNS_PAGE_SIZE,
          job: filterJob || null,
          status: filterStatus || null,
          trigger: filterTrigger || null
        });
        if (request !== historyRequest.current) return;
        setHistory(result);
        setHistoryError(null);
      } catch (cause) {
        if (request !== historyRequest.current) return;
        // Un error no borra lo que ya está en pantalla: se conserva la página y se ofrece reintentar.
        setHistoryError(cause instanceof Error ? cause.message : "No se pudo cargar el historial de jobs");
      } finally {
        if (request === historyRequest.current) setHistoryLoading(false);
      }
    },
    [filterJob, filterStatus, filterTrigger]
  );

  useEffect(() => {
    void loadCatalog();
  }, [loadCatalog]);

  useEffect(() => {
    void loadHistory(1);
  }, [loadHistory]);

  const selectedItem = useMemo(
    () => catalog?.items.find((item) => item.job === selectedJob) ?? null,
    [catalog, selectedJob]
  );

  // Polling acotado mientras corre la ejecución que acaba de pedir el usuario. No hay streaming en V1: se
  // consulta el mismo `jobRunId` hasta que termina, con un techo de intentos.
  //
  // Las dependencias son el id y las cargas, nunca el objeto de la ejecución: si `watched` estuviera en la
  // lista, cada respuesta reiniciaría el efecto (y su contador) y el sondeo no terminaría nunca.
  useEffect(() => {
    if (watchRunId === null) return;

    let cancelled = false;
    let attempts = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const tick = async () => {
      if (cancelled) return;
      attempts += 1;
      try {
        const run = await getJobRun(watchRunId);
        if (cancelled) return;
        setWatched(run);
        if (run.status !== "running") {
          setFeedback({
            kind: run.status === "ok" ? "success" : "error",
            message:
              run.status === "ok"
                ? `La ejecución #${run.jobRunId} terminó correctamente.`
                : `La ejecución #${run.jobRunId} terminó con fallos. Revisa el detalle.`,
            run
          });
          setWatchRunId(null);
          void loadHistory(1);
          void loadCatalog();
          return;
        }
      } catch {
        // Un error de sondeo no cancela el seguimiento: se reintenta hasta el techo.
      }

      if (attempts >= 60) {
        setFeedback({
          kind: "error",
          message: `La ejecución #${watchRunId} sigue en curso; deja de seguirla y consulta el historial.`
        });
        setWatchRunId(null);
        return;
      }

      timer = setTimeout(() => void tick(), 4_000);
    };

    timer = setTimeout(() => void tick(), 2_000);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [watchRunId, loadCatalog, loadHistory]);

  function selectJob(job: string) {
    setSelectedJob(job);
    setConfirmDestructive(false);
    setFeedback({ kind: "idle" });
    const item = catalog?.items.find((candidate) => candidate.job === job);
    const defaults: Record<string, boolean> = {};
    for (const option of item?.options ?? []) defaults[option.key] = option.defaultValue;
    setOptionValues(defaults);
  }

  async function submit(item: JobCatalogItem, dryRun: boolean) {
    const values = dryRun ? { ...optionValues, dryRun: true } : optionValues;
    const payload: JobRunPayload = buildJobRunPayload(item.options, values, confirmDestructive);
    setStarting(true);
    setFeedback({ kind: "idle" });
    try {
      const queued = await runJob(item.job, payload);
      setFeedback({ kind: "queued", queued });
      setWatched({
        jobRunId: queued.jobRunId,
        job: queued.job,
        trigger: "manual",
        status: "running",
        startedAt: queued.requestedAt ?? new Date().toISOString(),
        finishedAt: null,
        durationMilliseconds: null,
        details: null
      });
      setWatchRunId(queued.jobRunId);
      void loadCatalog();
    } catch (cause) {
      setFeedback({
        kind: "error",
        message: cause instanceof Error ? cause.message : "No se pudo iniciar la ejecución"
      });
    } finally {
      setStarting(false);
    }
  }

  function toggleRun(run: JobRun) {
    setExpandedRunId((current) => (current === run.jobRunId ? null : run.jobRunId));
  }

  const totalPages = history?.totalPages ?? 1;
  const page = clampJobPage(history?.page ?? 1, totalPages);
  const optionSummary = selectedItem ? formatJobOptionValues(selectedItem.options, optionValues) : [];
  const requiresAck = Boolean(selectedItem?.requiresConfirmation);
  const canRun = Boolean(selectedItem?.enabled) && !starting && (!requiresAck || confirmDestructive);
  const catalogJobs = catalog?.items ?? [];

  return (
    <AdminShell
      username={username}
      section="Administración"
      title="Jobs"
      subtitle="Historial, detalle completo y ejecución manual de los jobs registrados en este host."
    >
      <div className="space-y-4">
        <Card className="space-y-3 p-4" aria-labelledby="jobs-run-heading">
          <div className="space-y-1">
            <p className="text-xs font-semibold uppercase tracking-widest text-muted">A demanda</p>
            <h2 id="jobs-run-heading" className="text-lg font-semibold text-primary">
              Ejecutar un job
            </h2>
            <p className="text-xs text-muted">
              La ejecución se encola en este host y la petición devuelve un identificador: no se espera a que
              termine. Se usa el mismo candado y el mismo registro que la ejecución programada, así que no
              pueden solaparse. Un job deshabilitado se muestra con su motivo y no se puede lanzar.
            </p>
          </div>

          {catalogError ? <Alert variant="danger">{catalogError}</Alert> : null}

          <div className="grid gap-3 sm:grid-cols-2">
            <Select
              id="job-run-select"
              label="Job"
              value={selectedJob}
              onChange={(event) => selectJob(event.target.value)}
              disabled={catalogJobs.length === 0}
            >
              <option value="">Selecciona un job</option>
              {catalogJobs.map((item) => (
                <option key={item.job} value={item.job}>
                  {item.displayName}
                  {item.enabled ? "" : " (deshabilitado)"}
                </option>
              ))}
            </Select>

            {selectedItem ? (
              <div className="space-y-1 text-xs">
                <p className="flex flex-wrap items-center gap-1.5">
                  {selectedItem.enabled ? (
                    <span className="tabler-badge tabler-badge-muted">Habilitado</span>
                  ) : (
                    <span className="tabler-badge tabler-badge-warning">Deshabilitado</span>
                  )}
                  {selectedItem.running ? (
                    <span className="tabler-badge tabler-badge-info">En ejecución</span>
                  ) : null}
                  {selectedItem.destructive ? (
                    <span className="tabler-badge tabler-badge-danger">Borra datos</span>
                  ) : null}
                </p>
                <p className="text-muted">Programación: {selectedItem.schedule}</p>
                {selectedItem.disabledReason ? (
                  <p className="text-muted">{selectedItem.disabledReason}</p>
                ) : null}
              </div>
            ) : null}
          </div>

          {selectedItem ? <p className="text-sm text-secondary">{selectedItem.description}</p> : null}

          {selectedItem && selectedItem.options.length > 0 ? (
            <fieldset className="space-y-2 rounded-[var(--radius-md)] border border-default p-3">
              <legend className="px-1 text-xs font-semibold uppercase tracking-widest text-muted">
                Opciones
              </legend>
              {selectedItem.options.map((option) => (
                <label key={option.key} className="flex items-start gap-2 text-sm text-primary">
                  <input
                    type="checkbox"
                    className="mt-0.5 h-4 w-4 accent-[var(--color-accent)]"
                    checked={optionValues[option.key] ?? option.defaultValue}
                    onChange={(event) =>
                      setOptionValues((current) => ({ ...current, [option.key]: event.target.checked }))
                    }
                  />
                  <span>
                    <span className="font-medium">{option.label}</span>
                    {option.help ? <span className="block text-xs text-muted">{option.help}</span> : null}
                  </span>
                </label>
              ))}
            </fieldset>
          ) : null}

          {requiresAck ? (
            <label className="flex items-start gap-2 rounded-[var(--radius-md)] border border-[var(--color-danger)]/40 bg-[var(--color-danger)]/10 p-3 text-sm text-primary">
              <input
                type="checkbox"
                className="mt-0.5 h-4 w-4 accent-[var(--color-danger)]"
                checked={confirmDestructive}
                onChange={(event) => setConfirmDestructive(event.target.checked)}
              />
              <span>
                Entiendo que este job borra datos y quiero ejecutarlo. La simulación no borra nada y no
                requiere esta confirmación.
              </span>
            </label>
          ) : null}

          {selectedItem ? (
            <div className="flex flex-wrap items-center gap-2">
              <Button
                type="button"
                onClick={() => selectedItem && void submit(selectedItem, false)}
                loading={starting}
                loadingText="Enviando..."
                disabled={!canRun}
              >
                <Play className="h-4 w-4" aria-hidden="true" />
                Ejecutar ahora
              </Button>
              {selectedItem.destructive ? (
                <Button
                  type="button"
                  variant="secondary"
                  onClick={() => selectedItem && void submit(selectedItem, true)}
                  disabled={!selectedItem.enabled || starting}
                >
                  Simulación (no borra)
                </Button>
              ) : null}
              {selectedItem.options.length > 0 ? (
                <span className="text-xs text-muted">Opciones efectivas: {optionSummary.join(" · ")}</span>
              ) : null}
            </div>
          ) : null}

          <div className="space-y-2">
            {feedback.kind === "queued" ? (
              <Alert variant="info">
                <p>
                  Ejecución #{feedback.queued.jobRunId} en cola para {feedback.queued.job}.
                </p>
                <Button
                  type="button"
                  variant="ghost"
                  className="mt-1 h-8 px-2"
                  onClick={() => {
                    setExpandedRunId(feedback.queued.jobRunId);
                    setFilterJob(feedback.queued.job);
                  }}
                >
                  Ver en el historial
                </Button>
              </Alert>
            ) : null}
            {feedback.kind === "success" ? <Alert variant="info">{feedback.message}</Alert> : null}
            {feedback.kind === "error" ? <Alert variant="danger">{feedback.message}</Alert> : null}
            {watched && watched.status === "running" && watchRunId !== null ? (
              <p className="text-xs text-muted">
                Siguiendo la ejecución #{watched.jobRunId}... el resultado aparecerá aquí.
              </p>
            ) : null}
          </div>
        </Card>

        <Card className="space-y-3 p-4" aria-labelledby="jobs-catalog-heading">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <p className="text-xs font-semibold uppercase tracking-widest text-muted">Catálogo</p>
              <h2 id="jobs-catalog-heading" className="text-lg font-semibold text-primary">
                Jobs registrados ({catalogJobs.length})
              </h2>
            </div>
            <Button type="button" variant="secondary" onClick={() => void loadCatalog()}>
              <RefreshCw className="h-4 w-4" aria-hidden="true" />
              Actualizar catálogo
            </Button>
          </div>
          {catalogJobs.length === 0 && !catalogError ? (
            <p className="text-sm text-muted">Cargando catálogo...</p>
          ) : null}
          <div className="grid gap-2 lg:grid-cols-2">
            {catalogJobs.map((item) => (
              <CatalogCard
                key={item.job}
                item={item}
                onSelect={selectJob}
                // Filtra el historial por ese job y expande su última fila: así «Ver último» funciona
                // aunque la página actual del historial no lo contuviera.
                onShowLast={(run) => {
                  setFilterJob(run.job);
                  setExpandedRunId(run.jobRunId);
                }}
              />
            ))}
          </div>
        </Card>

        <Card className="space-y-3 p-4" aria-labelledby="jobs-history-heading">
          <div className="flex flex-wrap items-end justify-between gap-2">
            <div>
              <p className="text-xs font-semibold uppercase tracking-widest text-muted">Historial</p>
              <h2 id="jobs-history-heading" className="text-lg font-semibold text-primary">
                Ejecuciones
              </h2>
            </div>
            <div className="flex flex-wrap items-end gap-2">
              <Select
                id="job-history-filter"
                label="Job"
                value={filterJob}
                onChange={(event) => {
                  setFilterJob(event.target.value);
                  setExpandedRunId(null);
                }}
              >
                <option value="">Todos</option>
                {catalogJobs.map((item) => (
                  <option key={item.job} value={item.job}>
                    {item.displayName}
                  </option>
                ))}
              </Select>
              <Select
                id="job-status-filter"
                label="Estado"
                value={filterStatus}
                onChange={(event) => {
                  setFilterStatus(event.target.value);
                  setExpandedRunId(null);
                }}
              >
                <option value="">Todos</option>
                <option value="running">En ejecución</option>
                <option value="ok">Correcto</option>
                <option value="failed">Fallido</option>
              </Select>
              <Select
                id="job-trigger-filter"
                label="Disparo"
                value={filterTrigger}
                onChange={(event) => {
                  setFilterTrigger(event.target.value);
                  setExpandedRunId(null);
                }}
              >
                <option value="">Todos</option>
                <option value="scheduled">Programado</option>
                <option value="startup">Arranque</option>
                <option value="manual">Manual</option>
              </Select>
              <Button type="button" variant="secondary" onClick={() => void loadHistory(page)} loading={historyLoading}>
                <RefreshCw className="h-4 w-4" aria-hidden="true" />
                Actualizar
              </Button>
            </div>
          </div>

          {history ? (
            <div className="flex flex-wrap gap-1.5 text-xs">
              <span className="tabler-badge tabler-badge-muted">Total {history.summary.total}</span>
              <span className="tabler-badge tabler-badge-info">En ejecución {history.summary.running}</span>
              <span className="tabler-badge tabler-badge-success">Correctas {history.summary.ok}</span>
              <span className="tabler-badge tabler-badge-danger">Fallidas {history.summary.failed}</span>
              <span className="tabler-badge tabler-badge-muted">
                Última {formatDateTime(history.summary.lastStartedAt)}
              </span>
            </div>
          ) : null}

          {historyError ? (
            <div className="space-y-2">
              <Alert variant="danger">{historyError}</Alert>
              <Button type="button" variant="secondary" onClick={() => void loadHistory(page)}>
                Reintentar
              </Button>
            </div>
          ) : null}

          {historyLoading && !history ? <p className="text-sm text-muted">Cargando historial...</p> : null}

          {history && history.items.length === 0 && !historyError ? (
            <p className="text-sm text-muted">No hay ejecuciones que coincidan con el filtro.</p>
          ) : null}

          {history && history.items.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full text-left text-sm">
                <caption className="sr-only">
                  Ejecuciones de jobs con estado, disparo, duración y detalle expandible
                </caption>
                <thead>
                  <tr className="border-b border-strong text-muted">
                    <th scope="col" className="p-2">Ejecución</th>
                    <th scope="col" className="p-2">Job</th>
                    <th scope="col" className="p-2">Disparo</th>
                    <th scope="col" className="p-2">Estado</th>
                    <th scope="col" className="p-2">Inicio</th>
                    <th scope="col" className="p-2">Duración</th>
                    <th scope="col" className="p-2">Detalle</th>
                  </tr>
                </thead>
                <tbody>
                  {history.items.map((run) => {
                    const expanded = expandedRunId === run.jobRunId;
                    return (
                      <FragmentRow
                        key={run.jobRunId}
                        run={run}
                        expanded={expanded}
                        onToggle={() => toggleRun(run)}
                      />
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : null}

          {history && history.items.length > 0 ? (
            <nav className="flex flex-wrap items-center gap-2" aria-label="Paginación de ejecuciones">
              <Button
                type="button"
                variant="secondary"
                onClick={() => void loadHistory(page - 1)}
                disabled={historyLoading || page <= 1}
              >
                <ChevronLeft className="h-4 w-4" aria-hidden="true" />
                Anterior
              </Button>
              <span className="tabler-badge tabler-badge-muted">
                Página {page} de {totalPages}
              </span>
              <Button
                type="button"
                variant="secondary"
                onClick={() => void loadHistory(page + 1)}
                disabled={historyLoading || page >= totalPages}
              >
                Siguiente
                <ChevronRight className="h-4 w-4" aria-hidden="true" />
              </Button>
            </nav>
          ) : null}
        </Card>
      </div>
    </AdminShell>
  );
}

function FragmentRow({
  run,
  expanded,
  onToggle
}: {
  readonly run: JobRun;
  readonly expanded: boolean;
  readonly onToggle: () => void;
}) {
  const hasDetails = run.details !== null && run.details !== undefined;

  return (
    <>
      <tr className="border-b border-subtle align-top">
        <td className="p-2 font-mono text-xs text-muted">#{run.jobRunId}</td>
        <td className="p-2 font-medium text-primary">{run.job}</td>
        <td className="p-2 text-secondary">{triggerLabel(run.trigger)}</td>
        <td className="p-2"><StatusBadge status={run.status} /></td>
        <td className="p-2 text-secondary">{formatDateTime(run.startedAt)}</td>
        <td className="p-2 text-secondary">{formatDuration(run.durationMilliseconds)}</td>
        <td className="p-2">
          {hasDetails ? (
            <Button
              type="button"
              variant="ghost"
              className="h-8 px-2"
              aria-expanded={expanded}
              onClick={onToggle}
            >
              {expanded ? "Ocultar" : "Ver detalle"}
            </Button>
          ) : (
            <span className="text-xs text-muted">—</span>
          )}
        </td>
      </tr>
      {expanded ? (
        <tr className="border-b border-subtle">
          <td colSpan={7} className="p-3">
            <DetailsBlock run={run} />
          </td>
        </tr>
      ) : null}
    </>
  );
}
