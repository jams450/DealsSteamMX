// Reglas puras del resumen de importación (S1/S2/S4, odd/tasks/library-import-summary.md L2).
//
// El reporte del servidor es aditivo: los cuatro contadores legacy siguen siendo obligatorios y el detalle
// (`modified`, `deleted`, `addedItems`, `deletedItems`) es opcional. Estas pruebas fijan los dos lados del
// contrato que la UI no puede inventar:
//   · un payload viejo no se lee como "0 modificados": el detalle se declara no disponible,
//   · los nombres solo se pintan cuando la lista llega completa y su conteo es 1..50; con 51 o más, o con
//     una lista parcial, queda solo el número.
//
// Se prueba a través de `libraryImportSummary`, la proyección que consume el modal, nunca del estado interno.
import test from "node:test";
import assert from "node:assert/strict";
import {
  LIBRARY_IMPORT_DETAIL_LIMIT,
  libraryImportSummary,
  normalizeLibraryImportResponse,
  type LibraryImportReport
} from "./library-contract.ts";

// Reporte legacy: exactamente lo que responde un servidor anterior a esta tarea.
const legacy = { imported: 2, updated: 1, unresolved: 0, unsupportedSource: 0, byStore: { GOG: 2, Epic: 1 } };

function parse(input: unknown): LibraryImportReport {
  const report = normalizeLibraryImportResponse(input);
  assert.notEqual(report, null, `el reporte debería normalizar: ${JSON.stringify(input)}`);
  return report as LibraryImportReport;
}

function summary(input: unknown) {
  return libraryImportSummary(parse(input));
}

function names(count: number) {
  return Array.from({ length: count }, (_, index) => ({ title: `Juego ${index + 1}`, platform: "gog" }));
}

function detailed(overrides: Record<string, unknown> = {}) {
  return {
    imported: 2,
    updated: 1,
    unresolved: 0,
    unsupportedSource: 0,
    byStore: { GOG: 2 },
    modified: 1,
    deleted: 0,
    addedItems: names(2),
    deletedItems: [],
    ...overrides
  };
}

test("resumen: el orden del detalle es el del reporte aditivo (imported/updated/modified/deleted)", () => {
  assert.equal(LIBRARY_IMPORT_DETAIL_LIMIT, 50);
  const result = summary(detailed());
  assert.equal(result.detailed, true);
  assert.equal(result.imported, 2);
  assert.equal(result.updated, 1);
  assert.equal(result.modified, 1);
  assert.equal(result.deleted, 0);
  assert.equal(result.unresolved, 0);
  assert.equal(result.unsupportedSource, 0);
  assert.deepEqual(result.byStore, [{ store: "gog", count: 2 }]);
});

test("resumen: un payload viejo conserva los contadores y declara el detalle no disponible", () => {
  const result = summary(legacy);
  assert.equal(result.detailed, false);
  // La trampa que esta prueba cierra: `updated` no es `modified`. Un payload sin extras no puede leerse
  // como "0 modificados" ni como "1 modificado".
  assert.equal(result.updated, 1);
  assert.equal(result.modified, null);
  assert.equal(result.deleted, null);
  assert.deepEqual(result.addedNames, []);
  assert.deepEqual(result.deletedNames, []);
  // Los contadores legacy y el desglose siguen llegando.
  assert.equal(result.imported, 2);
  assert.deepEqual(result.byStore, [
    { store: "gog", count: 2 },
    { store: "epic", count: 1 }
  ]);
});

test("resumen: extras malformados no inventan modificaciones ni nombres", () => {
  for (const overrides of [
    { modified: -1 },
    { modified: "muchos" },
    { modified: 1.5 },
    { modified: null },
    { deleted: -1 },
    { deleted: null },
    // `Number(null)` es 0: la normalización no puede convertir un null en un cero inventado.
    { modified: null, deleted: 0 }
  ]) {
    const result = summary(detailed(overrides));
    assert.equal(result.detailed, false, `debe declararse no disponible con ${JSON.stringify(overrides)}`);
    assert.equal(result.modified, null);
    assert.equal(result.deleted, null);
    assert.deepEqual(result.addedNames, []);
    assert.deepEqual(result.deletedNames, []);
    assert.equal(result.updated, 1, "los contadores legacy no dependen del detalle");
  }
});

test("resumen: `modified` es el cambio real del servidor, independiente de `updated`", () => {
  const result = summary(detailed({ updated: 9, modified: 0 }));
  assert.equal(result.detailed, true);
  assert.equal(result.updated, 9);
  // Cero cambios reales es un dato, no una ausencia: se conserva el 0.
  assert.equal(result.modified, 0);
  assert.equal(result.deleted, 0);
});

test("resumen: nombres de agregados con 1..50 inclusive", () => {
  const one = summary(detailed({ imported: 1, addedItems: names(1) }));
  assert.deepEqual(one.addedNames, [{ title: "Juego 1", platform: "gog" }]);

  const fifty = summary(detailed({ imported: 50, addedItems: names(50) }));
  assert.equal(fifty.addedNames.length, 50);
  assert.equal(fifty.addedNames[49]?.title, "Juego 50");
});

test("resumen: con 51 o más agregados solo queda el número, sin lista parcial", () => {
  const countOnly = summary(detailed({ imported: 51, addedItems: [] }));
  assert.equal(countOnly.imported, 51);
  assert.deepEqual(countOnly.addedNames, []);

  // Aunque el servidor mandara los 51 nombres, el tope los esconde: la lista se omite por completo.
  const oversize = summary(detailed({ imported: 51, addedItems: names(51) }));
  assert.deepEqual(oversize.addedNames, []);
});

test("resumen: 0 agregados es lista vacía y no un detalle ausente", () => {
  const result = summary(detailed({ imported: 0, addedItems: [] }));
  assert.equal(result.detailed, true);
  assert.equal(result.imported, 0);
  assert.deepEqual(result.addedNames, []);
});

test("resumen: una lista de nombres parcial o con una fila inválida no se pinta", () => {
  const partial = summary(detailed({ imported: 3, addedItems: names(2) }));
  assert.equal(partial.imported, 3);
  assert.deepEqual(partial.addedNames, []);

  const invalidRow = summary(
    detailed({ imported: 3, addedItems: [names(1)[0], { title: "   ", platform: "gog" }, names(1)[0]] })
  );
  assert.deepEqual(invalidRow.addedNames, []);

  const notAnArray = summary(detailed({ imported: 2, addedItems: "Juego 1" }));
  assert.equal(notAnArray.detailed, true, "un detalle ausente no degrada los números");
  assert.equal(notAnArray.modified, 1);
  assert.deepEqual(notAnArray.addedNames, []);
});

test("resumen: el tope de nombres es independiente por categoría", () => {
  const result = summary(
    detailed({
      imported: 3,
      updated: 4,
      addedItems: names(3),
      deleted: 51,
      // El servidor no lista nada cuando pasa de 50, y aquí tampoco se pinta un nombre suelto.
      deletedItems: [{ title: "No debería verse", platform: "steam" }]
    })
  );
  assert.equal(result.deleted, 51);
  assert.equal(result.addedNames.length, 3);
  assert.deepEqual(result.deletedNames, []);

  // La otra dirección: sin eliminados no hay nada que nombrar, aunque el payload insista.
  const noDeletes = summary(detailed({ deleted: 0, deletedItems: names(1) }));
  assert.equal(noDeletes.deleted, 0);
  assert.deepEqual(noDeletes.deletedNames, []);
});

test("resumen: eliminados nombra plataforma en minúsculas y título acotado", () => {
  const result = summary(
    detailed({
      imported: 0,
      addedItems: [],
      deleted: 1,
      deletedItems: [{ title: "  Cyberpunk 2077  ", platform: "GOG" }]
    })
  );
  assert.deepEqual(result.deletedNames, [{ title: "Cyberpunk 2077", platform: "gog" }]);
});

test("resumen: la normalización del BFF es idempotente (ida y vuelta por JSON)", () => {
  const first = parse(detailed({ deleted: 1, deletedItems: [{ title: "Viejo", platform: "steam" }] }));
  const roundTripped = parse(JSON.parse(JSON.stringify(first)) as unknown);
  assert.deepEqual(libraryImportSummary(roundTripped), libraryImportSummary(first));

  const legacyReport = parse(legacy);
  const legacyRoundTrip = parse(JSON.parse(JSON.stringify(legacyReport)) as unknown);
  assert.equal(legacyRoundTrip.modified, null);
  assert.equal(libraryImportSummary(legacyRoundTrip).detailed, false);
});
