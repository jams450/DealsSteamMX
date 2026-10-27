"use client";

// Pestaña «Diagnóstico de sincronización» (spec S6): elige una ejecución histórica, pide su DETALLE saneado al
// endpoint existente (`GET /api/bff/jobs/{id}`) y pinta el resumen completo por proveedor/resultado/motivo más
// el detalle por juego.
//
// Lo que este componente NO hace:
//   · no ejecuta nada, ni por tienda ni por proveedor: solo lee una ejecución ya terminada;
//   · no lee el `details` del listado: el preview del historial no es el detalle saneado de la ejecución;
//   · no recalcula los contadores del resumen desde los 200 eventos acotados, y cuando el detalle filtrado es
//     parcial lo dice con los dos números en vez de presentarlo como el total;
//   · no pinta HTML, URLs ni mensajes del proveedor: el contrato solo admite tokens, ids, estados HTTP y
//     tipos de excepción acotados.
//
// La selección está numerada con `DiagnosticSelectionGuard`: si el usuario cambia de ejecución, la respuesta
// que llegue tarde no puede pintar el diagnóstico de otra.
import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import type { JobRun, JobsResponse } from "@/lib/contracts/jobs";
import {
  DiagnosticSelectionGuard,
  diagnosticElapsedText,
  diagnosticFilteredDetailNote,
  diagnosticFilterOptions,
  diagnosticGameHref,
  diagnosticHttpStatusText,
  diagnosticOutcomeLabel,
  diagnosticProviderLabel,
  diagnosticReasonLabel,
  diagnosticSummaryCount,
  diagnosticSummaryScopeNote,
  diagnosticsAvailabilityMessage,
  diagnosticsCompletenessNote,
  diagnosticsTruncationMessage,
  filterDiagnosticEvents,
  filterDiagnosticSummary,
  normalizeJobDiagnostics,
  parseDiagnosticProviderFilter,
  parseDiagnosticReasonFilter,
  type JobDiagnosticEvent,
  type JobDiagnostics,
  type JobDiagnosticsFilters
} from "@/lib/contracts/job-diagnostics";
import { getJobRun } from "./_lib/jobs-api";

const dateTimeFormatter = new Intl.DateTimeFormat("es-MX", { dateStyle: "short", timeStyle: "medium" });

const RUN_STATUS_LABELS: Record<string, string> = {
  running: "En ejecución",
  ok: "Correcto",
  failed: "Fallido"
};

function formatDateTime(value: string | null) {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : dateTimeFormatter.format(date);
}

function runStatusLabel(status: string) {
  return RUN_STATUS_LABELS[status] ?? status;
}

/**
 * Celda de juego. El enlace al detalle solo se pinta cuando la AppID de Steam es conocida; un `gameId`
 * canónico se muestra como id y nunca se traduce a `/games/<appId>` ni a una URL inventada.
 */
function GameCell({ event }: { readonly event: JobDiagnosticEvent }) {
  const href = diagnosticGameHref(event);
  if (href) {
    return (
      <Link
        href={href}
        className="text-accent hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
      >
        {`AppID ${event.appId}`}
      </Link>
    );
  }
  if (event.gameId !== null) {
    return <span className="text-muted">{`Juego #${event.gameId}`}</span>;
  }
  return <span className="text-muted">—</span>;
}

function SummaryTable({
  summary,
  total
}: {
  readonly summary: ReturnType<typeof filterDiagnosticSummary>;
  readonly total: number;
}) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-left text-sm">
        <caption className="sr-only">
          Resumen completo de la ejecución por proveedor, resultado y motivo, con el número de eventos de
          cada grupo
        </caption>
        <thead>
          <tr className="border-b border-strong text-muted">
            <th scope="col" className="p-2">Proveedor</th>
            <th scope="col" className="p-2">Resultado</th>
            <th scope="col" className="p-2">Motivo</th>
            <th scope="col" className="p-2">Eventos</th>
          </tr>
        </thead>
        <tbody>
          {summary.map((entry, index) => (
            <tr
              key={`${entry.provider}-${entry.outcome}-${entry.reason}-${index}`}
              className="border-b border-subtle align-top"
            >
              <td className="p-2 text-primary">
                {diagnosticProviderLabel(entry.provider)} <code className="text-xs text-muted">{entry.provider}</code>
              </td>
              <td className="p-2 text-secondary">
                {diagnosticOutcomeLabel(entry.outcome)} <code className="text-xs text-muted">{entry.outcome}</code>
              </td>
              <td className="p-2 text-secondary">
                {diagnosticReasonLabel(entry.reason)} <code className="text-xs text-muted">{entry.reason}</code>
              </td>
              <td className="p-2 font-medium text-primary">{entry.count}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr className="text-sm">
            <td colSpan={3} className="p-2 text-muted">Total de la selección</td>
            <td className="p-2 font-semibold text-primary">{total}</td>
          </tr>
        </tfoot>
      </table>
    </div>
  );
}

function EventsTable({ events }: { readonly events: ReturnType<typeof filterDiagnosticEvents> }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-left text-sm">
        <caption className="sr-only">
          Detalle por juego de la ejecución: identidad conocida, proveedor, resultado, motivo, estado HTTP, tipo
          de error y duración
        </caption>
        <thead>
          <tr className="border-b border-strong text-muted">
            <th scope="col" className="p-2">Juego</th>
            <th scope="col" className="p-2">Proveedor</th>
            <th scope="col" className="p-2">Resultado</th>
            <th scope="col" className="p-2">Motivo</th>
            <th scope="col" className="p-2">HTTP</th>
            <th scope="col" className="p-2">Tipo de error</th>
            <th scope="col" className="p-2">Duración</th>
          </tr>
        </thead>
        <tbody>
          {events.map((event, index) => (
            <tr key={`${event.appId ?? "no-app"}-${event.gameId ?? "no-game"}-${index}`} className="border-b border-subtle align-top">
              <td className="p-2 text-secondary"><GameCell event={event} /></td>
              <td className="p-2 text-secondary">
                {diagnosticProviderLabel(event.provider)} <code className="text-xs text-muted">{event.provider}</code>
              </td>
              <td className="p-2 text-secondary">
                {diagnosticOutcomeLabel(event.outcome)} <code className="text-xs text-muted">{event.outcome}</code>
              </td>
              <td className="p-2 text-secondary">
                {diagnosticReasonLabel(event.reason)} <code className="text-xs text-muted">{event.reason}</code>
              </td>
              <td className="p-2 text-secondary">{diagnosticHttpStatusText(event.httpStatus)}</td>
              <td className="p-2 text-secondary">
                {event.errorType === null ? "—" : <code className="text-xs">{event.errorType}</code>}
              </td>
              <td className="p-2 text-secondary">{diagnosticElapsedText(event.elapsedMs)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ReportView({
  report,
  selectedRunId
}: {
  readonly report: JobDiagnostics;
  readonly selectedRunId: number;
}) {
  const [providerFilter, setProviderFilter] = useState("");
  const [reasonFilter, setReasonFilter] = useState("");

  // Los filtros son de una ejecución: al cambiar de ejecución no se arrastran valores de la anterior.
  useEffect(() => {
    setProviderFilter("");
    setReasonFilter("");
  }, [selectedRunId]);

  const filters: JobDiagnosticsFilters = {
    provider: parseDiagnosticProviderFilter(providerFilter),
    reason: parseDiagnosticReasonFilter(reasonFilter)
  };
  const options = diagnosticFilterOptions(report);
  const summary = filterDiagnosticSummary(report, filters);
  const events = filterDiagnosticEvents(report, filters);
  const summaryTotal = diagnosticSummaryCount(summary);
  const truncation = diagnosticsTruncationMessage(report);
  const completeness = diagnosticsCompletenessNote(report);
  const filteredNote = diagnosticFilteredDetailNote(report, summaryTotal, events.length);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-1.5 text-xs">
        <span className="tabler-badge tabler-badge-muted">{`Ejecución #${selectedRunId}`}</span>
        <span className="tabler-badge tabler-badge-muted">{`${report.totalEvents} eventos`}</span>
        <span className="tabler-badge tabler-badge-info">{`Detalle ${report.shownEvents} de ${report.totalEvents}`}</span>
        {report.truncatedEvents > 0 ? (
          <span className="tabler-badge tabler-badge-warning">{`${report.truncatedEvents} sin detalle`}</span>
        ) : null}
      </div>

      {completeness ? <Alert variant="danger">{completeness}</Alert> : null}
      {truncation ? <Alert variant="info">{truncation}</Alert> : null}
      {filteredNote ? <Alert variant="info">{filteredNote}</Alert> : null}

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <Select
          id="diagnostics-provider-filter"
          label="Proveedor"
          value={providerFilter}
          onChange={(event) => setProviderFilter(event.target.value)}
        >
          <option value="">Todos</option>
          {options.providers.map((provider) => (
            <option key={provider} value={provider}>
              {diagnosticProviderLabel(provider)}
            </option>
          ))}
        </Select>
        <Select
          id="diagnostics-reason-filter"
          label="Motivo"
          value={reasonFilter}
          onChange={(event) => setReasonFilter(event.target.value)}
        >
          <option value="">Todos</option>
          {options.reasons.map((reason) => (
            <option key={reason} value={reason}>
              {diagnosticReasonLabel(reason)}
            </option>
          ))}
        </Select>
        <div className="flex items-end">
          <Button
            type="button"
            variant="ghost"
            onClick={() => {
              setProviderFilter("");
              setReasonFilter("");
            }}
            disabled={providerFilter === "" && reasonFilter === ""}
          >
            Limpiar filtros
          </Button>
        </div>
      </div>

      <section className="space-y-2" aria-labelledby="diagnostics-summary-heading">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h3 id="diagnostics-summary-heading" className="text-sm font-semibold text-primary">
            Resumen completo por proveedor y resultado
          </h3>
          <span className="tabler-badge tabler-badge-muted">{`${summaryTotal} eventos en la selección`}</span>
        </div>
        <p className="text-xs text-muted">
          Los contadores suman todos los eventos de la ejecución; no se recalculan desde el detalle acotado.
        </p>
        <p className="text-xs text-muted">{diagnosticSummaryScopeNote()}</p>
        {summary.length === 0 ? (
          <p className="text-sm text-muted">No hay grupos que coincidan con el filtro.</p>
        ) : (
          <SummaryTable summary={summary} total={summaryTotal} />
        )}
      </section>

      <section className="space-y-2" aria-labelledby="diagnostics-events-heading">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h3 id="diagnostics-events-heading" className="text-sm font-semibold text-primary">
            Detalle por juego
          </h3>
          <span className="tabler-badge tabler-badge-muted">{`${events.length} visibles`}</span>
        </div>
        <p className="text-xs text-muted">
          Cada fila es un evento guardado por la sincronización. Un enlace solo aparece cuando la AppID de Steam
          es conocida; un id canónico no se traduce a una ruta de Steam.
        </p>
        {events.length === 0 ? (
          <p className="text-sm text-muted">
            {report.shownEvents === 0
              ? "Esta ejecución no guardó eventos de detalle."
              : "No hay eventos de detalle que coincidan con el filtro. Los contadores del resumen siguen siendo válidos."}
          </p>
        ) : (
          <EventsTable events={events} />
        )}
      </section>
    </div>
  );
}

export function DiagnosticsPanel({
  history,
  historyLoading,
  historyError,
  onReloadHistory,
  selectedRunId,
  onSelectRun
}: {
  readonly history: JobsResponse | null;
  readonly historyLoading: boolean;
  readonly historyError: string | null;
  readonly onReloadHistory: () => void;
  readonly selectedRunId: number | null;
  readonly onSelectRun: (jobRunId: number | null) => void;
}) {
  const [guard] = useState(() => new DiagnosticSelectionGuard());
  const [detail, setDetail] = useState<JobRun | null>(null);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [loadingDetail, setLoadingDetail] = useState(false);
  const [reloadToken, setReloadToken] = useState(0);

  useEffect(() => {
    if (selectedRunId === null) {
      guard.invalidate();
      setDetail(null);
      setDetailError(null);
      setLoadingDetail(false);
      return;
    }

    const token = guard.begin();
    let cancelled = false;
    // Un cambio de ejecución no puede seguir mostrando el detalle de la anterior mientras llega el nuevo.
    setDetail((current) => (current && current.jobRunId === selectedRunId ? current : null));
    setDetailError(null);
    setLoadingDetail(true);

    void getJobRun(selectedRunId)
      .then((run) => {
        if (cancelled || !guard.isCurrent(token)) return;
        setDetail(run);
      })
      .catch((cause: unknown) => {
        if (cancelled || !guard.isCurrent(token)) return;
        setDetail(null);
        setDetailError(cause instanceof Error ? cause.message : "No se pudo cargar la ejecución");
      })
      .finally(() => {
        if (!cancelled && guard.isCurrent(token)) setLoadingDetail(false);
      });

    return () => {
      cancelled = true;
      guard.invalidate();
    };
  }, [selectedRunId, reloadToken, guard]);

  // Doble candado contra una respuesta atrasada: además del guard, solo se pinta el detalle de la ejecución
  // que está seleccionada ahora mismo.
  const reportSource = detail && detail.jobRunId === selectedRunId ? detail : null;
  const result = useMemo(
    () => normalizeJobDiagnostics(reportSource ? reportSource.details : null),
    [reportSource]
  );

  const runs = history?.items ?? [];

  return (
    <div className="space-y-3">
      <div className="space-y-1">
        <h2 id="jobs-diagnostics-heading" className="text-lg font-semibold text-primary">
          Diagnóstico de sincronización
        </h2>
        <p className="text-xs text-muted">
          Lectura de una ejecución ya registrada: cuántos eventos terminaron en cada resultado y motivo, tienda
          por tienda, y el detalle por juego. No ejecuta nada; el resumen es completo y el detalle se limita a
          los primeros 200 eventos que guardó la ejecución.
        </p>
      </div>

      <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-end">
        <Select
          id="diagnostics-run"
          label="Ejecución"
          value={selectedRunId === null ? "" : String(selectedRunId)}
          onChange={(event) => {
            const next = Number(event.target.value);
            onSelectRun(Number.isInteger(next) && next > 0 ? next : null);
          }}
          disabled={runs.length === 0 && !historyLoading}
        >
          <option value="">{historyLoading && !history ? "Cargando ejecuciones..." : "Selecciona una ejecución"}</option>
          {runs.map((run) => (
            <option key={run.jobRunId} value={run.jobRunId}>
              {`#${run.jobRunId} · ${run.job} · ${formatDateTime(run.startedAt)} · ${runStatusLabel(run.status)}`}
            </option>
          ))}
        </Select>
        <Button
          type="button"
          variant="secondary"
          onClick={() => setReloadToken((current) => current + 1)}
          disabled={selectedRunId === null || loadingDetail}
        >
          Recargar diagnóstico
        </Button>
      </div>

      <p className="text-xs text-muted">
        El selector lista las ejecuciones de la página actual del historial; cambia de página en «Ejecuciones»
        para llegar a otra.
      </p>

      {historyError ? (
        <div className="space-y-2">
          <Alert variant="danger">{historyError}</Alert>
          <Button type="button" variant="secondary" onClick={() => onReloadHistory()}>
            Reintentar historial
          </Button>
        </div>
      ) : null}

      <p className="sr-only" aria-live="polite">
        {loadingDetail
          ? "Cargando el diagnóstico de la ejecución seleccionada."
          : selectedRunId === null
            ? "Selecciona una ejecución para leer su diagnóstico."
            : result.kind === "available"
              ? `Diagnóstico cargado: ${result.report.totalEvents} eventos, ${result.report.shownEvents} con detalle.`
              : "La ejecución seleccionada no tiene un diagnóstico legible."}
      </p>

      {selectedRunId === null ? (
        <p className="text-sm text-muted">
          Selecciona una ejecución del historial para leer su diagnóstico. Las ejecuciones viejas o de jobs que
          no sincronizan no tienen reporte: eso se muestra como «sin diagnóstico», nunca como cero fallos.
        </p>
      ) : null}

      {selectedRunId !== null && loadingDetail && result.kind === "absent" ? (
        <p className="text-sm text-muted">Cargando ejecución...</p>
      ) : null}

      {selectedRunId !== null && detailError ? (
        <div className="space-y-2">
          <Alert variant="danger">{detailError}</Alert>
          <Button type="button" variant="secondary" onClick={() => setReloadToken((current) => current + 1)}>
            Reintentar detalle
          </Button>
        </div>
      ) : null}

      {selectedRunId !== null && !detailError && result.kind === "available" ? (
        <ReportView report={result.report} selectedRunId={selectedRunId} />
      ) : null}

      {selectedRunId !== null && !detailError && result.kind !== "available" && !loadingDetail ? (
        <Alert variant="info">{diagnosticsAvailabilityMessage(result)}</Alert>
      ) : null}
    </div>
  );
}
