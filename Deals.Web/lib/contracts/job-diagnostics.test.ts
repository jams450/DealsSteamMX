// Contrato del diagnóstico de sincronización (spec S5/S6/S7). Los normalizadores son la única puerta entre
// `job_runs.details` y la pestaña: un esquema desconocido no puede leerse como «0 fallos», un contador
// negativo no puede convertirse en 0, y ningún mensaje/URL del proveedor puede viajar hasta la UI.
// Se ejecuta con `node --test lib/contracts/job-diagnostics.test.ts` (sin instalar nada).
import test from "node:test";
import assert from "node:assert/strict";
import {
  MAX_DIAGNOSTIC_EVENTS,
  MAX_DIAGNOSTIC_SUMMARY_GROUPS,
  DiagnosticSelectionGuard,
  diagnosticElapsedText,
  diagnosticFilterOptions,
  diagnosticFilteredDetailNote,
  diagnosticGameHref,
  diagnosticHttpStatusText,
  diagnosticOutcomeLabel,
  diagnosticProviderLabel,
  diagnosticReasonLabel,
  diagnosticSummaryCount,
  diagnosticsAvailabilityMessage,
  diagnosticsCompletenessNote,
  diagnosticSummaryScopeNote,
  diagnosticsTruncationMessage,
  filterDiagnosticEvents,
  filterDiagnosticSummary,
  normalizeJobDiagnostics,
  parseDiagnosticProviderFilter,
  parseDiagnosticReasonFilter,
  type JobDiagnosticEvent,
  type JobDiagnostics
} from "./job-diagnostics.ts";

const EVENT = {
  appId: 1057800,
  gameId: 9,
  provider: "epic",
  outcome: "refreshed",
  reason: "refreshed",
  httpStatus: 200,
  errorType: null,
  elapsedMs: 12
};

function envelope(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 1,
    totalEvents: 1,
    shownEvents: 1,
    truncatedEvents: 0,
    summary: [{ provider: "epic", outcome: "refreshed", reason: "refreshed", httpStatus: 200, count: 1 }],
    events: [EVENT],
    ...overrides
  };
}

function read(report: Record<string, unknown>) {
  const result = normalizeJobDiagnostics({ diagnostics: report });
  assert.equal(result.kind, "available");
  return (result as { kind: "available"; report: JobDiagnostics }).report;
}

test("reads diagnostics from details.diagnostics and from the runner wrapper details.result.diagnostics", () => {
  const direct = normalizeJobDiagnostics({ diagnostics: envelope() });
  const wrapped = normalizeJobDiagnostics({ result: { diagnostics: envelope() } });
  const pascal = normalizeJobDiagnostics({
    Result: {
      Diagnostics: {
        SchemaVersion: 1,
        TotalEvents: 1,
        ShownEvents: 1,
        TruncatedEvents: 0,
        Summary: [{ Provider: "epic", Outcome: "refreshed", Reason: "refreshed", HttpStatus: 200, Count: 1 }],
        Events: [
          {
            AppId: 1057800,
            GameId: null,
            Provider: "epic",
            Outcome: "refreshed",
            Reason: "refreshed",
            HttpStatus: 200,
            ErrorType: null,
            ElapsedMs: 12
          }
        ]
      }
    }
  });

  for (const result of [direct, wrapped, pascal]) {
    assert.equal(result.kind, "available");
    if (result.kind !== "available") return;
    assert.equal(result.report.schemaVersion, 1);
    assert.equal(result.report.totalEvents, 1);
    assert.equal(result.report.events[0].appId, 1057800);
  }
});

test("missing, null and foreign details are absent, never a zero-failure report", () => {
  for (const details of [null, undefined, {}, { error: "boom" }, { diagnostics: null }, { result: { diagnostics: null } }]) {
    assert.deepEqual(normalizeJobDiagnostics(details), { kind: "absent" });
  }
});

test("an unknown or missing schema is reported honestly and is never interpreted", () => {
  assert.deepEqual(normalizeJobDiagnostics({ diagnostics: { ...envelope(), schemaVersion: 2 } }), {
    kind: "unsupported",
    schemaVersion: 2
  });
  // Un esquema v1 sin versión no se asume.
  const { schemaVersion: _omitted, ...withoutVersion } = envelope();
  assert.deepEqual(normalizeJobDiagnostics({ diagnostics: withoutVersion }), { kind: "malformed" });
  assert.deepEqual(normalizeJobDiagnostics({ diagnostics: { ...envelope(), schemaVersion: "1" } }), { kind: "malformed" });
});

test("summary counts stay complete when the event detail is capped at 200", () => {
  const events = Array.from({ length: 250 }, (_, index) => ({ ...EVENT, appId: index + 1 }));
  const report = read(
    envelope({
      totalEvents: 350,
      shownEvents: 250,
      truncatedEvents: 100,
      summary: [{ provider: "epic", outcome: "refreshed", reason: "refreshed", httpStatus: 200, count: 350 }],
      events
    })
  );

  assert.equal(report.events.length, MAX_DIAGNOSTIC_EVENTS);
  assert.equal(report.shownEvents, MAX_DIAGNOSTIC_EVENTS);
  assert.equal(report.totalEvents, 350);
  assert.equal(report.truncatedEvents, 150);
  // El resumen NO se recalcula desde los 200 eventos mostrados.
  assert.equal(report.summary[0].count, 350);
  assert.equal(diagnosticSummaryCount(report.summary), 350);
  assert.match(diagnosticsTruncationMessage(report) ?? "", /completos/);
});

test("dishonest counters are corrected instead of claiming more events than the envelope has", () => {
  const report = read(envelope({ totalEvents: 5, shownEvents: 5, truncatedEvents: 0, events: [EVENT, EVENT] }));
  assert.equal(report.shownEvents, 2);
  assert.equal(report.totalEvents, 5);
  assert.equal(report.truncatedEvents, 3);

  // Un `truncatedEvents` mayor que el total no se repite: shown + truncated siempre suma el total.
  const inflated = read(envelope({ totalEvents: 2, shownEvents: 2, truncatedEvents: 99, events: [EVENT, EVENT] }));
  assert.equal(inflated.totalEvents, 2);
  assert.equal(inflated.shownEvents, 2);
  assert.equal(inflated.truncatedEvents, 0);
});

test("negative or fractional counts make the report unreadable instead of becoming zero", () => {
  assert.deepEqual(normalizeJobDiagnostics({ diagnostics: envelope({ totalEvents: -1 }) }), { kind: "malformed" });
  assert.deepEqual(normalizeJobDiagnostics({ diagnostics: envelope({ shownEvents: 1.5 }) }), { kind: "malformed" });
  assert.deepEqual(normalizeJobDiagnostics({ diagnostics: envelope({ truncatedEvents: "0" }) }), { kind: "malformed" });
});

test("unknown fields and unsafe strings are discarded from an event", () => {
  const report = read(
    envelope({
      events: [
        {
          ...EVENT,
          appId: "1057800",
          gameId: -4,
          httpStatus: 999,
          elapsedMs: -5,
          errorType: "<img src=x onerror=alert(1)>",
          message: "System.Exception: secret token",
          url: "https://provider.invalid/?key=secret",
          error: "raw failure",
          stack: "at Foo.Bar()"
        }
      ]
    })
  );

  assert.deepEqual(report.events[0], {
    appId: null,
    gameId: null,
    provider: "epic",
    outcome: "refreshed",
    reason: "refreshed",
    httpStatus: null,
    errorType: null,
    elapsedMs: null
  });
  // Ningún texto crudo del proveedor sobrevive al normalizador.
  assert.deepEqual(Object.keys(report.events[0]).sort(), [
    "appId",
    "elapsedMs",
    "errorType",
    "gameId",
    "httpStatus",
    "outcome",
    "provider",
    "reason"
  ]);
  assert.equal(diagnosticHttpStatusText(report.events[0].httpStatus), "—");
  assert.equal(diagnosticElapsedText(report.events[0].elapsedMs), "—");
});

test("an event with an unknown provider, outcome or reason is dropped and shownEvents stays honest", () => {
  const report = read(
    envelope({
      totalEvents: 3,
      shownEvents: 3,
      truncatedEvents: 0,
      events: [EVENT, { ...EVENT, provider: "steam; drop table" }, { ...EVENT, reason: "made_up" }]
    })
  );

  assert.equal(report.events.length, 1);
  assert.equal(report.shownEvents, 1);
  assert.equal(report.totalEvents, 3);
  assert.equal(report.truncatedEvents, 2);
});

test("an invalid summary group makes the whole report unreadable", () => {
  for (const entry of [
    { provider: "steam;--", outcome: "refreshed", reason: "refreshed", httpStatus: null, count: 1 },
    { provider: "steam", outcome: "exploded", reason: "refreshed", httpStatus: null, count: 1 },
    { provider: "steam", outcome: "refreshed", reason: "whatever", httpStatus: null, count: 1 },
    { provider: "steam", outcome: "refreshed", reason: "refreshed", httpStatus: null, count: -3 }
  ]) {
    assert.deepEqual(normalizeJobDiagnostics({ diagnostics: envelope({ summary: [entry] }) }), { kind: "malformed" });
  }
});

test("valid identifiers survive and the errorType is bounded to a plausible type name", () => {
  const kept = read(
    envelope({
      events: [
        {
          ...EVENT,
          httpStatus: 429,
          errorType: "System.Threading.Tasks.TaskCanceledException",
          elapsedMs: 0
        }
      ]
    })
  ).events[0];

  assert.equal(kept.httpStatus, 429);
  assert.equal(kept.errorType, "System.Threading.Tasks.TaskCanceledException");
  assert.equal(kept.elapsedMs, 0);
  assert.equal(diagnosticElapsedText(0), "0 ms");
  assert.equal(diagnosticElapsedText(1512), "1.5 s");

  const tooLong = read(envelope({ events: [{ ...EVENT, errorType: `E${"x".repeat(80)}` }] })).events[0];
  assert.equal(tooLong.errorType, null);
});

test("filters narrow summary and events independently while the aggregate count stays complete", () => {
  const report = read(
    envelope({
      totalEvents: 17,
      shownEvents: 2,
      truncatedEvents: 15,
      summary: [
        { provider: "epic", outcome: "refreshed", reason: "refreshed", httpStatus: 200, count: 5 },
        { provider: "steam", outcome: "failed", reason: "http_error", httpStatus: 403, count: 10 },
        { provider: "ggdeals", outcome: "no_match", reason: "store_no_offer", httpStatus: null, count: 2 }
      ],
      events: [EVENT, { ...EVENT, provider: "steam", outcome: "failed", reason: "http_error", httpStatus: 403 }]
    })
  );

  const epic = { provider: "epic" as const, reason: null };
  assert.equal(filterDiagnosticSummary(report, epic).length, 1);
  assert.equal(diagnosticSummaryCount(filterDiagnosticSummary(report, epic)), 5);
  // El detalle acotado no tiene ningún evento de ggdeals, y aun así su contador del resumen existe.
  const ggdeals = { provider: "ggdeals" as const, reason: null };
  assert.equal(filterDiagnosticEvents(report, ggdeals).length, 0);
  assert.equal(diagnosticSummaryCount(filterDiagnosticSummary(report, ggdeals)), 2);

  assert.deepEqual(diagnosticFilterOptions(report).providers, ["steam", "ggdeals", "epic"]);
  assert.deepEqual(diagnosticFilterOptions(report).reasons, [
    "refreshed",
    "http_error",
    "store_no_offer"
  ]);
  assert.equal(parseDiagnosticProviderFilter("epic"), "epic");
  assert.equal(parseDiagnosticProviderFilter("unknown"), null);
  assert.equal(parseDiagnosticReasonFilter("http_error"), "http_error");
  assert.equal(parseDiagnosticReasonFilter("steam"), null);
});

test("filtered detail is announced as partial and never confused with the full aggregate", () => {
  const report = read(
    envelope({ totalEvents: 20, shownEvents: 2, truncatedEvents: 18, events: [EVENT, EVENT] })
  );
  const note = diagnosticFilteredDetailNote(report, 20, 2);

  assert.match(note ?? "", /parcial/);
  assert.match(note ?? "", /20 eventos/);
  assert.match(note ?? "", /aquí se ven 2/);
  // Sin truncamiento el detalle coincide: no se inventa un aviso.
  assert.equal(diagnosticFilteredDetailNote(read(envelope()), 1, 1), null);
});

test("Spanish labels keep the machine token available for support", () => {
  assert.equal(diagnosticProviderLabel("epic"), "Epic Games Store");
  assert.equal(diagnosticProviderLabel("itad-identity"), "ITAD identidad");
  assert.equal(diagnosticOutcomeLabel("no_match"), "Sin coincidencia");
  assert.equal(diagnosticReasonLabel("http_error"), "Error HTTP");
  assert.equal(diagnosticReasonLabel("title_fallback_disabled"), "Búsqueda por título deshabilitada");
  assert.equal(diagnosticReasonLabel("gate_skipped"), "Omitido por frescura");
});

test("availability messages never present an unreadable report as zero failures", () => {
  const absent = diagnosticsAvailabilityMessage({ kind: "absent" });
  const unsupported = diagnosticsAvailabilityMessage({ kind: "unsupported", schemaVersion: 3 });
  const malformed = diagnosticsAvailabilityMessage({ kind: "malformed" });

  for (const message of [absent, unsupported, malformed]) {
    // El reporte ilegible se declara como tal; nunca se presenta "0 fallos" como resultado.
    assert.doesNotMatch(message, /^(0|cero) fallos/i);
    assert.match(message, /no significa|no se contabiliza/i);
  }
  assert.match(absent, /no guardó un diagnóstico/);
  assert.match(unsupported, /esquema 3/);
  assert.match(malformed, /incompleto|contrato/);
});

test("a null summary status means aggregated, not unknown, and the detail keeps the real HTTP status", () => {
  const report = read(
    envelope({
      totalEvents: 9,
      shownEvents: 2,
      truncatedEvents: 7,
      summary: [{ provider: "epic", outcome: "failed", reason: "http_error", httpStatus: null, count: 9 }],
      events: [
        { ...EVENT, outcome: "failed", reason: "http_error", httpStatus: 429 },
        { ...EVENT, outcome: "failed", reason: "http_error", httpStatus: 403 }
      ]
    })
  );

  // El resumen agrupa los nueve eventos fallidos sin importar el estado: el contador no se pierde.
  assert.equal(report.summary[0].httpStatus, null);
  assert.equal(report.summary[0].count, 9);
  assert.equal(
    diagnosticSummaryCount(filterDiagnosticSummary(report, { provider: null, reason: "http_error" })),
    9
  );
  // El estado real sigue viajando en el detalle por juego.
  assert.deepEqual(report.events.map((event) => event.httpStatus), [429, 403]);
  assert.equal(diagnosticHttpStatusText(report.events[0].httpStatus), "429");
  assert.equal(diagnosticHttpStatusText(report.events[1].httpStatus), "403");
  // La UI no puede leer ese null como «no se conoce el estado»: lo dice la nota del resumen.
  assert.match(diagnosticSummaryScopeNote(), /detalle por juego/);
  assert.doesNotMatch(diagnosticSummaryScopeNote(), /desconocid/i);
});

test("an oversized summary is flagged instead of claimed as complete", () => {
  const summary = Array.from({ length: MAX_DIAGNOSTIC_SUMMARY_GROUPS + 1 }, () => ({
    provider: "steam",
    outcome: "refreshed",
    reason: "refreshed",
    httpStatus: 200,
    count: 1
  }));
  const report = read(envelope({ summary, totalEvents: MAX_DIAGNOSTIC_SUMMARY_GROUPS + 1 }));

  assert.equal(report.summary.length, MAX_DIAGNOSTIC_SUMMARY_GROUPS);
  assert.equal(report.summaryTruncated, true);
  assert.match(diagnosticsCompletenessNote(report) ?? "", /no son completos/);
  assert.equal(diagnosticsCompletenessNote(read(envelope())), null);
});

test("a game link requires a known Steam appid and is never inferred", () => {
  const event: JobDiagnosticEvent = { ...EVENT, appId: null, gameId: 42 };
  assert.equal(diagnosticGameHref(event), null);
  assert.equal(diagnosticGameHref({ ...EVENT, appId: 1057800 }), "/games/1057800");
});

test("the selection guard drops an out-of-order detail response", () => {
  const guard = new DiagnosticSelectionGuard();
  const first = guard.begin();
  const second = guard.begin();

  // La respuesta del primer pedido llega tarde: ya no es la vigente.
  assert.equal(guard.isCurrent(first), false);
  assert.equal(guard.isCurrent(second), true);

  guard.invalidate();
  assert.equal(guard.isCurrent(second), false);
});
