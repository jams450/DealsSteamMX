import test from "node:test";
import assert from "node:assert/strict";
import {
  normalizeCoverSyncReport,
  normalizeCoverUrl,
  normalizeSteamGridDbCoverSearch,
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

test("normalizeCoverSyncReport: exige los ocho contadores", () => {
  const report = {
    missing: 412,
    updated: 25,
    updatedBySteam: 18,
    updatedByIgdb: 5,
    updatedBySteamGridDb: 2,
    unmatched: 79,
    failed: 1,
    remaining: 307
  };
  assert.deepEqual(normalizeCoverSyncReport(report), report);

  // Un contador ausente, negativo o no entero invalida el reporte entero: la UI no puede inventar el que
  // falta ni pintar la mitad de una pasada.
  assert.equal(normalizeCoverSyncReport({ ...report, failed: undefined }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, updatedBySteamGridDb: undefined }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, updated: -1 }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, unmatched: -1 }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, remaining: 1.5 }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, updatedByIgdb: 2.5 }), null);
  assert.equal(normalizeCoverSyncReport({ ...report, missing: "412" }), null);
  assert.equal(normalizeCoverSyncReport(null), null);
});

test("parseCoverPick: exactamente uno de los tres ids", () => {
  assert.deepEqual(parseCoverPick({ steamAppId: 1091500 }), { steamAppId: 1091500 });
  assert.deepEqual(parseCoverPick({ igdbId: 101999 }), { igdbId: 101999 });
  assert.deepEqual(parseCoverPick({ steamGridDbId: 42 }), { steamGridDbId: 42 });

  // Un campo nulo o ausente cuenta como "no enviado", la misma convención que el backend.
  assert.deepEqual(parseCoverPick({ steamAppId: 620, igdbId: null }), { steamAppId: 620 });
  assert.deepEqual(parseCoverPick({ igdbId: 967140, steamAppId: null }), { igdbId: 967140 });
  assert.deepEqual(parseCoverPick({ steamGridDbId: 42, steamAppId: null, igdbId: null }), { steamGridDbId: 42 });

  // Dos o más a la vez es ambiguo y ninguno no elige nada: ambos casos se rechazan.
  assert.equal(parseCoverPick({ steamAppId: 620, igdbId: 967140 }), null);
  assert.equal(parseCoverPick({ steamGridDbId: 1, steamAppId: 620 }), null);
  assert.equal(parseCoverPick({ steamGridDbId: 1, igdbId: 967140 }), null);
  assert.equal(parseCoverPick({ steamGridDbId: 1, steamAppId: 620, igdbId: 967140 }), null);
  assert.equal(parseCoverPick({}), null);
  assert.equal(parseCoverPick({ steamAppId: null, igdbId: null }), null);
  assert.equal(parseCoverPick({ steamAppId: null, igdbId: null, steamGridDbId: null }), null);

  // Un id que no sea número entero positivo se rechaza, nunca se recorta ni se convierte.
  assert.equal(parseCoverPick({ steamAppId: 0 }), null);
  assert.equal(parseCoverPick({ steamAppId: -7 }), null);
  assert.equal(parseCoverPick({ steamAppId: 1.5 }), null);
  assert.equal(parseCoverPick({ steamAppId: "1091500" }), null);
  assert.equal(parseCoverPick({ igdbId: 0 }), null);
  assert.equal(parseCoverPick({ igdbId: "967140" }), null);
  assert.equal(parseCoverPick({ steamGridDbId: 0 }), null);
  assert.equal(parseCoverPick({ steamGridDbId: -3 }), null);
  assert.equal(parseCoverPick({ steamGridDbId: 1.5 }), null);
  assert.equal(parseCoverPick({ steamGridDbId: "42" }), null);

  // Un campo desconocido es cuerpo inválido: el backend lo devuelve como 400, no lo ignora. Tampoco se
  // acepta una URL ni una variante PascalCase de los tres campos.
  assert.equal(parseCoverPick({ steamAppId: 620, imageUrl: "https://cdn.example/a.jpg" }), null);
  assert.equal(parseCoverPick({ igdbId: 967140, title: "Metroid Dread" }), null);
  assert.equal(parseCoverPick({ steamGridDbId: 1, imageUrl: "x" }), null);
  assert.equal(parseCoverPick({ SteamAppId: 620 }), null);
  assert.equal(parseCoverPick({ SteamGridDbId: 42 }), null);

  assert.equal(parseCoverPick([620]), null);
  assert.equal(parseCoverPick(null), null);
  assert.equal(parseCoverPick("620"), null);
  assert.equal(parseCoverPick(620), null);
});

test("normalizeSteamGridDbCoverSearch: exige source y candidatos bien formados", () => {
  const candidate = { id: 42, name: "Floppy Knights", verified: true };
  const search = { source: "steamgriddb", candidates: [candidate] };
  assert.deepEqual(normalizeSteamGridDbCoverSearch(search), search);

  // `source: null` es «proveedor no disponible»: no se confunde con «sin candidatos». Por el cable llega como
  // llave ausente (el API no escribe los nulos) y el normalizador las trata igual.
  assert.deepEqual(normalizeSteamGridDbCoverSearch({ source: null, candidates: [] }), {
    source: null,
    candidates: []
  });

  // La respuesta vacía es un resultado legítimo del proveedor.
  assert.deepEqual(
    normalizeSteamGridDbCoverSearch({ source: "steamgriddb", candidates: [] }),
    { source: "steamgriddb", candidates: [] }
  );

  // Un candidato sin nombre, con id inválido o con `verified` no booleano invalida la respuesta entera.
  assert.equal(normalizeSteamGridDbCoverSearch({ source: "steamgriddb", candidates: [{}] }), null);
  assert.equal(
    normalizeSteamGridDbCoverSearch({ source: "steamgriddb", candidates: [{ id: 42, verified: true }] }),
    null
  );
  assert.equal(
    normalizeSteamGridDbCoverSearch({ source: "steamgriddb", candidates: [{ id: 0, name: "x", verified: true }] }),
    null
  );
  assert.equal(
    normalizeSteamGridDbCoverSearch({ source: "steamgriddb", candidates: [{ id: 42, name: "", verified: true }] }),
    null
  );
  assert.equal(
    normalizeSteamGridDbCoverSearch({ source: "steamgriddb", candidates: [{ id: 42, name: "x", verified: "true" }] }),
    null
  );
  assert.equal(
    normalizeSteamGridDbCoverSearch({ source: "steamgriddb", candidates: [candidate, { name: "y" }] }),
    null
  );

  // El API no escribe los miembros nulos, así que «no disponible» llega como llave ausente: es la misma
  // respuesta que `source: null`, nunca una carga inválida.
  assert.deepEqual(normalizeSteamGridDbCoverSearch({ candidates: [] }), { source: null, candidates: [] });

  // `source` mal formado (número o cadena vacía) y formas que no son el objeto documentado.
  assert.equal(normalizeSteamGridDbCoverSearch({ source: 42, candidates: [] }), null);
  assert.equal(normalizeSteamGridDbCoverSearch({ source: "  ", candidates: [] }), null);
  assert.equal(normalizeSteamGridDbCoverSearch({ source: null, candidates: "nada" }), null);
  assert.equal(normalizeSteamGridDbCoverSearch({ source: null }), null);
  assert.equal(normalizeSteamGridDbCoverSearch(null), null);
  assert.equal(normalizeSteamGridDbCoverSearch([]), null);
  assert.equal(normalizeSteamGridDbCoverSearch("steamgriddb"), null);
});

test("resolveCoverSource: solo PC propone Steam, solo consola propone IGDB, mixto nada", () => {
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
