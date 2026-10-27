import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

// La pestaña «Diagnóstico de sincronización» es un componente cliente: no hay runner de React en este repo,
// así que estas comprobaciones estructurales fijan el cableado que el contrato
// (`lib/contracts/job-diagnostics.test.ts`) sí prueba de comportamiento: la ejecución se pide al endpoint
// de detalle, los filtros salen del resumen completo, el detalle acotado se anuncia como tal y la pestaña
// no ejecuta nada por tienda.
const client = new URL("../app/jobs/jobs-client.tsx", import.meta.url);
const panel = new URL("../app/jobs/diagnostics-panel.tsx", import.meta.url);

async function source(url) {
  return readFile(url, "utf8");
}

test("Jobs exposes the exact diagnostics tab as an accessible tablist", async () => {
  const text = await source(client);

  assert.match(text, /Diagnóstico de sincronización/);
  assert.match(text, /role="tablist"/);
  assert.match(text, /aria-label="Secciones de Jobs"/);
  assert.match(text, /role="tab"/);
  assert.match(text, /aria-selected=\{/);
  assert.match(text, /aria-controls=\{/);
  // Los ids son estables y derivan del token de la pestaña (`jobs-tab-runs`, `jobs-panel-diagnostics`...).
  assert.match(text, /id=\{`jobs-tab-\$\{tab\}`\}/);
  assert.match(text, /id="jobs-panel-runs"/);
  assert.match(text, /id="jobs-panel-diagnostics"/);
  assert.match(text, /role="tabpanel"/);
  assert.match(text, /hidden=\{/);
  // Roving tabindex + teclado, igual que las demás pestañas de la app.
  assert.match(text, /tabIndex=\{/);
  for (const key of ["ArrowRight", "ArrowLeft", "Home", "End"]) {
    assert.match(text, new RegExp(`"${key}"`));
  }
});

test("the diagnostics tab fetches the selected historical run detail, not the list preview", async () => {
  const text = await source(panel);

  assert.match(text, /import \{[^}]*getJobRun[^}]*\} from "\.\/_lib\/jobs-api"/s);
  assert.match(text, /getJobRun\(selectedRunId\)/);
  assert.match(text, /useEffect\(\(\) => \{[\s\S]*?selectedRunId[\s\S]*?\}, \[/);
  // El detalle del listado no es el detalle saneado de la ejecución: `history` solo alimenta el selector.
  assert.doesNotMatch(text, /history\.items\.find/);
  assert.doesNotMatch(text, /history\.items\[[^\]]*\]\.details/);
});

test("run selection is guarded so an out-of-order response cannot paint another run", async () => {
  const text = await source(panel);

  assert.match(text, /DiagnosticSelectionGuard/);
  assert.match(text, /const token = guard\.begin\(\)/);
  assert.match(text, /guard\.isCurrent\(token\)/);
  assert.match(text, /guard\.invalidate\(\)/);
  // La respuesta solo se pinta si pertenece a la ejecución seleccionada.
  assert.match(text, /detail\.jobRunId === selectedRunId/);
});

test("provider and reason filters are labelled and derived from the complete summary", async () => {
  const text = await source(panel);

  assert.match(text, /diagnosticFilterOptions\(report\)/);
  assert.match(text, /label="Proveedor"/);
  assert.match(text, /label="Motivo"/);
  assert.match(text, /parseDiagnosticProviderFilter\(/);
  assert.match(text, /parseDiagnosticReasonFilter\(/);
  assert.match(text, /filterDiagnosticSummary\(report, filters\)/);
  assert.match(text, /filterDiagnosticEvents\(report, filters\)/);
  assert.match(text, /diagnosticSummaryCount\(/);
  assert.match(text, /diagnosticProviderLabel\(/);
  assert.match(text, /diagnosticReasonLabel\(/);
});

test("the bounded detail is announced as truncation and never confused with the aggregate", async () => {
  const text = await source(panel);

  assert.match(text, /diagnosticsTruncationMessage\(report\)/);
  assert.match(text, /diagnosticFilteredDetailNote\(report, /);
  assert.match(text, /diagnosticsCompletenessNote\(report\)/);
  // Los contadores del encabezado salen del sobre, no de los eventos mostrados.
  assert.match(text, /report\.totalEvents/);
  assert.match(text, /report\.truncatedEvents/);
  assert.doesNotMatch(text, /events\.length\s*\+\s*.*truncated/);
});

test("an unreadable or old run reports an honest no-diagnostic state", async () => {
  const text = await source(panel);

  assert.match(text, /normalizeJobDiagnostics\(/);
  assert.match(text, /diagnosticsAvailabilityMessage\(/);
  assert.match(text, /result\.kind === "available"/);
  // Un reporte ausente/corrupto no se pinta como «0 eventos» ni como éxito.
  assert.doesNotMatch(text, /0 fallos/);
  assert.doesNotMatch(text, /report\.totalEvents \?\? 0/);
});

test("the diagnostics tab never executes a job and never adds a per-store button", async () => {
  const text = await source(panel);

  assert.doesNotMatch(text, /runJob\(/);
  assert.doesNotMatch(text, /buildJobRunPayload/);
  assert.doesNotMatch(text, /job\/run/);
  assert.doesNotMatch(text, /Ejecutar ahora/);
  assert.doesNotMatch(text, /type="checkbox"/);
});

test("diagnostics render only text, never HTML or a raw provider URL", async () => {
  const panelText = await source(panel);
  const clientText = await source(client);

  for (const text of [panelText, clientText]) {
    assert.doesNotMatch(text, /dangerouslySetInnerHTML/);
    assert.doesNotMatch(text, /\.innerHTML/);
  }
  // Los códigos se muestran como texto en <code>, y el único enlace lo decide `diagnosticGameHref`.
  assert.match(panelText, /<code[^>]*>\{event\.outcome\}<\/code>/);
  assert.match(panelText, /<code[^>]*>\{event\.reason\}<\/code>/);
  assert.match(panelText, /diagnosticGameHref\(event\)/);
  assert.doesNotMatch(panelText, /href=\{event\./);
});

test("the existing jobs execution UI and boolean options stay in place", async () => {
  const text = await source(client);

  assert.match(text, /Ejecutar ahora/);
  assert.match(text, /buildJobRunPayload\(/);
  assert.match(text, /type="checkbox"/);
  assert.match(text, /jobs-run-heading/);
  assert.match(text, /jobs-history-heading/);
  assert.match(text, /Actualizar catálogo/);
});

test("the diagnostics tables and filters keep the accessible, mobile layout", async () => {
  const text = await source(panel);

  assert.match(text, /overflow-x-auto/);
  assert.match(text, /<caption[^>]*>/);
  assert.match(text, /scope="col"/);
  assert.match(text, /aria-live="polite"/);
  assert.match(text, /tabler-badge/);
});

test("the summary does not pretend a null HTTP status is unknown; the detail keeps the real status", async () => {
  const text = await source(panel);
  const summaryStart = text.indexOf("function SummaryTable(");
  const eventsStart = text.indexOf("function EventsTable(");

  assert.notEqual(summaryStart, -1);
  assert.notEqual(eventsStart, -1);
  const summary = text.slice(summaryStart, eventsStart);

  // El resumen agrupa por proveedor/resultado/motivo y suma todos los estados HTTP: no lleva columna HTTP
  // (un `null` no significa «no se conoce el estado»).
  assert.doesNotMatch(summary, /<th scope="col" className="p-2">HTTP<\/th>/);
  assert.doesNotMatch(summary, /diagnosticHttpStatusText\(entry\.httpStatus\)/);
  // La aclaración se pinta desde el contrato, no como copy paralela.
  assert.match(text, /diagnosticSummaryScopeNote\(\)/);

  // El detalle por juego conserva el estado HTTP real de cada evento.
  const events = text.slice(eventsStart);
  assert.match(events, /<th scope="col" className="p-2">HTTP<\/th>/);
  assert.match(events, /diagnosticHttpStatusText\(event\.httpStatus\)/);
});
