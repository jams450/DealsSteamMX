import test from "node:test";
import assert from "node:assert/strict";
import {
  normalizeCoverSyncReport,
  normalizeCoverUrl,
  parseCoverPick,
  parseCoverSyncRequest
} from "./library-covers.ts";

test("parseCoverSyncRequest: sin límite, límite válido o rechazo", () => {
  assert.deepEqual(parseCoverSyncRequest({}), {});
  assert.deepEqual(parseCoverSyncRequest({ limit: 25 }), { limit: 25 });
  assert.deepEqual(parseCoverSyncRequest({ limit: 100 }), { limit: 100 });

  // Fuera de rango se rechaza, no se recorta: el cliente nunca debe creer que pidió otra cosa.
  assert.equal(parseCoverSyncRequest({ limit: 0 }), null);
  assert.equal(parseCoverSyncRequest({ limit: 101 }), null);
  assert.equal(parseCoverSyncRequest({ limit: 2.5 }), null);
  assert.equal(parseCoverSyncRequest({ limit: "25" }), null);
  assert.equal(parseCoverSyncRequest(null), null);
  assert.equal(parseCoverSyncRequest(42), null);
});

test("normalizeCoverSyncReport: exige los cinco contadores", () => {
  const report = { missing: 412, missingWithoutSteamId: 307, updated: 25, failed: 1, remaining: 79 };
  assert.deepEqual(normalizeCoverSyncReport(report), report);

  assert.equal(normalizeCoverSyncReport({ ...report, failed: undefined }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, updated: -1 }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, remaining: 1.5 }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, missing: "412" }), null);
  assert.equal(normalizeCoverSyncReport(null), null);
});

test("parseCoverPick: un appid entero positivo y nada más", () => {
  assert.deepEqual(parseCoverPick({ steamAppId: 1091500 }), { steamAppId: 1091500 });
  assert.equal(parseCoverPick({ steamAppId: 0 }), null);
  assert.equal(parseCoverPick({ steamAppId: -7 }), null);
  assert.equal(parseCoverPick({ steamAppId: "1091500" }), null);
  assert.equal(parseCoverPick({}), null);
  assert.equal(parseCoverPick(null), null);
});

test("normalizeCoverUrl: solo una URL no vacía cuenta", () => {
  assert.equal(normalizeCoverUrl({ imageUrl: "https://cdn.example/a.jpg" }), "https://cdn.example/a.jpg");
  assert.equal(normalizeCoverUrl({ imageUrl: "   " }), null);
  assert.equal(normalizeCoverUrl({ imageUrl: 12 }), null);
  assert.equal(normalizeCoverUrl({}), null);
  assert.equal(normalizeCoverUrl(null), null);
});
