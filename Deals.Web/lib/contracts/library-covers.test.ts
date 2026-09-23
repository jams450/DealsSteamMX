import test from "node:test";
import assert from "node:assert/strict";
import {
  normalizeCoverSyncReport,
  normalizeCoverUrl,
  parseCoverPick,
  parseCoverSyncRequest,
  resolveCoverSource
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

test("parseCoverPick: exactamente uno de los dos ids", () => {
  assert.deepEqual(parseCoverPick({ steamAppId: 1091500 }), { steamAppId: 1091500 });
  assert.deepEqual(parseCoverPick({ igdbId: 101999 }), { igdbId: 101999 });

  // Un campo nulo o ausente cuenta como "no enviado", la misma convención que el backend.
  assert.deepEqual(parseCoverPick({ steamAppId: 620, igdbId: null }), { steamAppId: 620 });
  assert.deepEqual(parseCoverPick({ igdbId: 967140, steamAppId: null }), { igdbId: 967140 });

  // Los dos a la vez es ambiguo y ninguno no elige nada: ambos casos se rechazan.
  assert.equal(parseCoverPick({ steamAppId: 620, igdbId: 967140 }), null);
  assert.equal(parseCoverPick({}), null);
  assert.equal(parseCoverPick({ steamAppId: null, igdbId: null }), null);

  // Un id que no sea número entero positivo se rechaza, nunca se recorta ni se convierte.
  assert.equal(parseCoverPick({ steamAppId: 0 }), null);
  assert.equal(parseCoverPick({ steamAppId: -7 }), null);
  assert.equal(parseCoverPick({ steamAppId: 1.5 }), null);
  assert.equal(parseCoverPick({ steamAppId: "1091500" }), null);
  assert.equal(parseCoverPick({ igdbId: 0 }), null);
  assert.equal(parseCoverPick({ igdbId: "967140" }), null);

  // Un campo desconocido es cuerpo inválido: el backend lo devuelve como 400, no lo ignora. Tampoco se
  // acepta una URL ni una variante PascalCase de los dos campos.
  assert.equal(parseCoverPick({ steamAppId: 620, imageUrl: "https://cdn.example/a.jpg" }), null);
  assert.equal(parseCoverPick({ igdbId: 967140, title: "Metroid Dread" }), null);
  assert.equal(parseCoverPick({ SteamAppId: 620 }), null);

  assert.equal(parseCoverPick([620]), null);
  assert.equal(parseCoverPick(null), null);
  assert.equal(parseCoverPick("620"), null);
  assert.equal(parseCoverPick(620), null);
});

test("resolveCoverSource: solo PC Steam, solo consola IGDB, mixto a mano", () => {
  assert.equal(resolveCoverSource(["steam"]), "steam");
  assert.equal(resolveCoverSource(["epic", "gog"]), "steam");
  assert.equal(resolveCoverSource(["gog"]), "steam");

  assert.equal(resolveCoverSource(["switch"]), "igdb");
  assert.equal(resolveCoverSource(["ps5", "xbox-one"]), "igdb");

  // Mixto: la portada es una sola y no se adivina de la primera fila, así que elige el usuario.
  assert.equal(resolveCoverSource(["steam", "ps5"]), null);
  assert.equal(resolveCoverSource(["gog", "switch"]), null);

  // Sin plataformas no hay fuente que imponer: también se elige a mano.
  assert.equal(resolveCoverSource([]), null);
});

test("normalizeCoverUrl: solo una URL no vacía cuenta", () => {
  assert.equal(normalizeCoverUrl({ imageUrl: "https://cdn.example/a.jpg" }), "https://cdn.example/a.jpg");
  assert.equal(normalizeCoverUrl({ imageUrl: "   " }), null);
  assert.equal(normalizeCoverUrl({ imageUrl: 12 }), null);
  assert.equal(normalizeCoverUrl({}), null);
  assert.equal(normalizeCoverUrl(null), null);
});
