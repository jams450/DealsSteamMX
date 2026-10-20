import test from "node:test";
import assert from "node:assert/strict";
import {
  buildJobRunPayload,
  clampJobPage,
  formatJobOptionValues,
  JOB_RUNS_PAGE_SIZE,
  normalizeJobCatalog,
  normalizeJobRun,
  normalizeJobs
} from "../lib/contracts/jobs.ts";

// Contrato del panel de Jobs (S2). Los normalizadores son la única puerta entre el JSON del backend y la
// UI: un campo ausente o basura no puede convertirse en "0 ejecuciones" ni en una opción inventada.

test("normalizeJobCatalog keeps only honest entries", () => {
  const catalog = normalizeJobCatalog({
    items: [
      {
        job: "bundles.global-sync",
        displayName: "Bundles globales",
        description: "Barrido del catálogo de bundles de ITAD.",
        enabled: true,
        disabledReason: null,
        schedule: "cada 12 h",
        running: false,
        destructive: false,
        requiresConfirmation: false,
        options: [{ key: "includePriceRefresh", label: "Refrescar precios", help: "Tarda", defaultValue: false }],
        lastRun: { jobRunId: 7, job: "bundles.global-sync", status: "ok", trigger: "scheduled", startedAt: "2026-10-19T10:13:15Z", finishedAt: "2026-10-19T10:13:16Z", durationMilliseconds: 1410, details: { bundles: 12 } }
      },
      { job: "ntfy.alerts", displayName: "Alertas ntfy", description: "Avisos.", enabled: false, disabledReason: "Ntfy:Enabled está en false.", schedule: "cada 15 min", running: false, destructive: false, requiresConfirmation: false, options: [], lastRun: null },
      { displayName: "sin clave" },
      null
    ],
    generatedAt: "2026-10-19T10:20:00Z"
  });

  assert.equal(catalog.items.length, 2);
  assert.equal(catalog.items[0].job, "bundles.global-sync");
  assert.equal(catalog.items[0].options.length, 1);
  assert.equal(catalog.items[0].lastRun?.jobRunId, 7);
  assert.equal(catalog.items[1].enabled, false);
  assert.match(catalog.items[1].disabledReason ?? "", /false/);
  assert.equal(catalog.items[1].lastRun, null);
  assert.equal(catalog.generatedAt, "2026-10-19T10:20:00Z");
});

test("normalizeJobCatalog defaults disabled reasons to null and options to empty", () => {
  const catalog = normalizeJobCatalog({ items: [{ job: "fx.rate-refresh", displayName: "FX", description: "Tipo de cambio", enabled: true, schedule: "cada 24 h" }] });
  assert.equal(catalog.items[0].disabledReason, null);
  assert.deepEqual(catalog.items[0].options, []);
  assert.equal(catalog.items[0].running, false);
  assert.equal(catalog.items[0].lastRun, null);
  assert.equal(catalog.generatedAt, null);
  assert.deepEqual(normalizeJobCatalog(null).items, []);
});

test("normalizeJobRun rejects a row without a numeric run id", () => {
  assert.equal(normalizeJobRun(null), null);
  assert.equal(normalizeJobRun({ job: "x", startedAt: "2026-10-19T10:00:00Z" }), null);
  const run = normalizeJobRun({ jobRunId: 12, job: "jobs.purge", trigger: "manual", status: "failed", startedAt: "2026-10-19T10:00:00Z", finishedAt: null, durationMilliseconds: null, details: { error: "boom" } });
  assert.equal(run?.jobRunId, 12);
  assert.equal(run?.durationMilliseconds, null);
  assert.deepEqual(run?.details, { error: "boom" });
});

test("normalizeJobs keeps the existing history contract and drops unrenderable rows", () => {
  const page = normalizeJobs({
    summary: { total: 3, running: 1, ok: 1, failed: 1, lastStartedAt: "2026-10-19T10:00:00Z" },
    items: [
      { jobRunId: 1, job: "jobs.purge", trigger: "scheduled", status: "ok", startedAt: "2026-10-19T10:00:00Z", finishedAt: "2026-10-19T10:00:01Z", durationMilliseconds: 1000, details: null },
      { jobRunId: 2, job: "jobs.purge" },
      { job: "jobs.purge", startedAt: "2026-10-19T10:00:00Z" }
    ],
    page: 2,
    pageSize: 10,
    totalPages: 4,
    filters: { job: "jobs.purge", status: null, trigger: null }
  });
  assert.equal(page.summary.total, 3);
  assert.equal(page.items.length, 1);
  assert.equal(page.items[0].durationMilliseconds, 1000);
  assert.equal(page.page, 2);
  assert.equal(page.totalPages, 4);
  assert.deepEqual(page.filters, { job: "jobs.purge", status: null, trigger: null });
  assert.equal(normalizeJobs(undefined).page, 1);
  assert.equal(normalizeJobs(undefined).pageSize, 25);
  assert.deepEqual(normalizeJobs(undefined).filters, { job: null, status: null, trigger: null });
});

test("buildJobRunPayload sends only declared options and never an undeclared key", () => {
  const options = [
    { key: "produce", label: "Generar", help: "", defaultValue: true },
    { key: "dispatch", label: "Enviar", help: "", defaultValue: true }
  ];
  assert.deepEqual(buildJobRunPayload(options, {}, false), { options: { produce: true, dispatch: true }, confirm: false });
  assert.deepEqual(buildJobRunPayload(options, { dispatch: false }, true), { options: { produce: true, dispatch: false }, confirm: true });
  assert.deepEqual(buildJobRunPayload([], { produce: false }, false), { options: {}, confirm: false });
  assert.deepEqual(buildJobRunPayload(options, { unknown: false }, false), { options: { produce: true, dispatch: true }, confirm: false });
});

test("formatJobOptionValues reads back the effective options for the pending card", () => {
  const options = [{ key: "dryRun", label: "Simulación", help: "", defaultValue: false }];
  assert.deepEqual(formatJobOptionValues(options, {}), ["Simulación: no"]);
  assert.deepEqual(formatJobOptionValues(options, { dryRun: true }), ["Simulación: sí"]);
});

test("clampJobPage keeps pagination inside the server range", () => {
  assert.equal(clampJobPage(1, 5), 1);
  assert.equal(clampJobPage(0, 5), 1);
  assert.equal(clampJobPage(9, 5), 5);
  assert.equal(clampJobPage(3, 1), 1);
  assert.equal(clampJobPage(Number.NaN, 2), 1);
  assert.equal(JOB_RUNS_PAGE_SIZE, 20);
});
