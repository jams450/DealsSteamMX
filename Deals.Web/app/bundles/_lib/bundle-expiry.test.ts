import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import {
  EXPIRY_CRITICAL_MAX_DAYS,
  EXPIRY_WARNING_MAX_DAYS,
  bundleExpiryView,
  expiryDaysRemaining
} from "./bundle-expiry.ts";

// S1: "colocar etiqueta de días en rojo amarillo y verde con base a rojo 3 días, amarillo 7 días y verde
// más de 7 días". Los días restantes son `ceil((expiresAt - ahora) / 24h)` y el color NUNCA es el único
// canal: el badge lleva el número de días y una palabra de estado. El orden ya es global en el servidor
// (pre-paginación), así que este módulo no ordena nada; solo describe una fecha contra un reloj dado.
//
// Todo se mide con un `now` fijo: el módulo es puro y no lee el reloj de la máquina. Si leyera
// `Date.now()`, estas pruebas no podrían fijar los límites de 3 y 7 días.

const DAY_MS = 24 * 60 * 60 * 1000;
const NOW = Date.parse("2026-10-20T12:00:00Z");

function at(offsetMs: number): string {
  return new Date(NOW + offsetMs).toISOString();
}

function viewAt(offsetMs: number) {
  return bundleExpiryView(at(offsetMs), NOW);
}

// --- límites de días (ceil) -------------------------------------------------

test("el día restante se redondea hacia arriba: 23h ya cuenta como 1 día", () => {
  assert.equal(viewAt(DAY_MS - 1).daysRemaining, 1);
  assert.equal(viewAt(DAY_MS).daysRemaining, 1, "exactamente 24h sigue siendo 1 día");
  assert.equal(viewAt(DAY_MS + 1).daysRemaining, 2, "un milisegundo más ya es 2 días");
  assert.equal(expiryDaysRemaining(at(DAY_MS + 1), NOW), 2);
});

test("rojo hasta 3 días, incluido el límite exacto", () => {
  assert.equal(EXPIRY_CRITICAL_MAX_DAYS, 3, "el límite rojo es el que pidió el usuario");
  for (const days of [1, 2, 3]) {
    const view = viewAt(days * DAY_MS);
    assert.equal(view.daysRemaining, days);
    assert.equal(view.status, "critical", `${days} días debe ser rojo`);
    assert.equal(view.tone, "danger");
    assert.equal(view.badgeClass, "tabler-badge tabler-badge-danger");
  }
});

test("amarillo de 4 a 7 días, y verde recién a los 8", () => {
  assert.equal(EXPIRY_WARNING_MAX_DAYS, 7, "el límite amarillo es el que pidió el usuario");
  for (const days of [4, 5, 6, 7]) {
    const view = viewAt(days * DAY_MS);
    assert.equal(view.daysRemaining, days);
    assert.equal(view.status, "soon", `${days} días debe ser amarillo`);
    assert.equal(view.tone, "warning");
    assert.equal(view.badgeClass, "tabler-badge tabler-badge-warning");
  }
  for (const days of [8, 30, 365]) {
    const view = viewAt(days * DAY_MS);
    assert.equal(view.daysRemaining, days);
    assert.equal(view.status, "comfortable", `${days} días debe ser verde`);
    assert.equal(view.tone, "success");
    assert.equal(view.badgeClass, "tabler-badge tabler-badge-success");
  }
});

test("el borde de cada umbral es el milisegundo, no el día redondeado", () => {
  // 3 días + 1ms redondea a 4 días y por tanto sale del rojo; 7 días + 1ms sale del amarillo.
  assert.equal(viewAt(3 * DAY_MS + 1).status, "soon");
  assert.equal(viewAt(7 * DAY_MS + 1).status, "comfortable");
  // Y al revés: un milisegundo menos nunca baja de categoría.
  assert.equal(viewAt(3 * DAY_MS + 1 - 1).status, "critical");
  assert.equal(viewAt(7 * DAY_MS + 1 - 1).status, "soon");
});

// --- expirado ---------------------------------------------------------------

test("un bundle expirado se dice expirado y no se pinta como cuenta regresiva", () => {
  for (const offset of [0, -1, -DAY_MS, -30 * DAY_MS]) {
    const view = viewAt(offset);
    assert.equal(view.status, "expired", `offset ${offset} debe estar expirado`);
    assert.equal(view.daysRemaining, null, "no queda día que contar");
    assert.equal(view.label, "Expirado");
    assert.equal(view.tone, "neutral");
    assert.equal(view.badgeClass, "tabler-badge tabler-badge-muted");
    assert.equal(expiryDaysRemaining(at(offset), NOW), null);
  }
});

test("expirado nunca se lee como verde y no comparte texto con los días restantes", () => {
  const expired = viewAt(-1);
  assert.doesNotMatch(expired.label, /^Expira en/);
  const comfortable = viewAt(30 * DAY_MS);
  assert.notEqual(expired.label, comfortable.label);
  assert.notEqual(expired.badgeClass, comfortable.badgeClass);
});

// --- fecha ausente o ilegible ----------------------------------------------

test("sin fecha de expiración el estado es neutro, no verde", () => {
  for (const missing of [null, undefined, "", "   "]) {
    const view = bundleExpiryView(missing, NOW);
    assert.equal(view.status, "missing", `«${String(missing)}» no es una fecha`);
    assert.equal(view.daysRemaining, null);
    assert.equal(view.tone, "neutral");
    assert.equal(view.badgeClass, "tabler-badge tabler-badge-muted");
    assert.equal(view.label, "Sin fecha de expiración");
    assert.notEqual(view.tone, "success");
    assert.equal(expiryDaysRemaining(missing, NOW), null);
  }
});

test("una fecha ilegible es neutra y se distingue de una fecha ausente", () => {
  for (const invalid of ["no-es-fecha", "2026-13-45T00:00:00Z", "2026-10-32"]) {
    const view = bundleExpiryView(invalid, NOW);
    assert.equal(view.status, "invalid", `«${invalid}» debe ser ilegible`);
    assert.equal(view.daysRemaining, null);
    assert.equal(view.tone, "neutral");
    assert.equal(view.badgeClass, "tabler-badge tabler-badge-muted");
    assert.equal(view.label, "Fecha de expiración inválida");
    assert.equal(expiryDaysRemaining(invalid, NOW), null);
  }
  assert.notEqual(bundleExpiryView("no-es-fecha", NOW).label, bundleExpiryView(null, NOW).label);
});

test("un reloj no finito no inventa un color: se declara ilegible", () => {
  const view = bundleExpiryView(at(DAY_MS), Number.NaN);
  assert.equal(view.status, "invalid");
  assert.equal(view.tone, "neutral");
});

// --- texto: los días se leen, el color no es el único canal -----------------

test("el badge dice los días explícitos y una palabra de estado", () => {
  assert.equal(viewAt(DAY_MS).label, "Expira en 1 día · Urgente");
  assert.equal(viewAt(2 * DAY_MS).label, "Expira en 2 días · Urgente");
  assert.equal(viewAt(7 * DAY_MS).label, "Expira en 7 días · Pronto");
  assert.equal(viewAt(8 * DAY_MS).label, "Expira en 8 días · Con tiempo");

  // Singular/plural correctos y la palabra de estado presente en las tres franjas de color.
  assert.match(viewAt(DAY_MS).label, /1 día\b/);
  assert.doesNotMatch(viewAt(DAY_MS).label, /1 días/);
  for (const offset of [DAY_MS, 5 * DAY_MS, 30 * DAY_MS]) {
    const view = viewAt(offset);
    assert.match(view.label, /Expira en \d+ días? · \S/, "días + estado, nunca solo el color");
  }
});

test("cada estado tiene un texto distinto y no vacío", () => {
  const labels = [viewAt(DAY_MS), viewAt(5 * DAY_MS), viewAt(30 * DAY_MS), viewAt(-1), bundleExpiryView(null, NOW), bundleExpiryView("no-es-fecha", NOW)].map(
    (view) => view.label
  );
  assert.equal(new Set(labels).size, labels.length, "dos estados no comparten etiqueta");
  for (const label of labels) {
    assert.ok(label.trim().length > 0);
  }
});

// --- zona horaria -----------------------------------------------------------

test("la fecha se compara como instante, no como texto ni como día local", () => {
  // 2026-10-21T12:00:00-06:00 es 2026-10-21T18:00Z: 30h después del reloj fijo ⇒ 2 días.
  const view = bundleExpiryView("2026-10-21T12:00:00-06:00", NOW);
  assert.equal(view.daysRemaining, 2);
  // La misma cifra sin offset se interpreta como UTC por `Date.parse`, un instante distinto.
  assert.equal(bundleExpiryView("2026-10-21T18:00:00Z", NOW).daysRemaining, 2);
  assert.equal(bundleExpiryView("2026-10-21T12:00:00Z", NOW).daysRemaining, 1);
});

// --- el módulo es puro y el cliente lo cablea bien --------------------------

const read = (path: string) => readFile(new URL(path, import.meta.url), "utf8");

test("el módulo no lee el reloj ni ordena: todo entra por parámetro", async () => {
  const source = await read("./bundle-expiry.ts");
  // El comentario puede nombrar `Date.now()` para explicar por qué no se usa; se miran solo las líneas
  // de código para que la prueba mida el módulo y no su documentación.
  const code = source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
  assert.equal(/Date\.now\s*\(/.test(code), false, "el reloj entra como argumento, no se lee aquí");
  assert.equal(/new Date\s*\(\s*\)/.test(code), false, "nada de fecha «ahora» implícita");
  assert.equal(/\.sort\s*\(|\.toSorted\s*\(|\.reverse\s*\(/.test(code), false, "el orden es del servidor");
});

test("el reloj es uno solo para todas las tarjetas, late acotado y se limpia", async () => {
  const source = await read("../bundles-client.tsx");
  const client = source.slice(source.indexOf("export function BundlesClient("));
  assert.ok(client.length > 0, "no se encontró BundlesClient en el cliente");

  // Un único intervalo (nada de un timer por tarjeta) con período acotado.
  assert.equal(client.match(/setInterval\s*\(/g)?.length, 1, "un solo intervalo compartido");
  assert.equal(/setInterval\s*\(\s*[^,]+,\s*EXPIRY_CLOCK_INTERVAL_MS\s*\)/.test(client), true);
  assert.match(source, /const EXPIRY_CLOCK_INTERVAL_MS = 60_000;/, "un minuto: lo mínimo que cambia un día");
  assert.match(client, /clearInterval\s*\(/, "el intervalo se limpia al desmontar");
  assert.match(client, /return \(\) => clearInterval/, "la limpieza se devuelve del efecto");

  // El HTML del servidor no puede saber la hora del cliente: el reloj arranca en el efecto (null) y la
  // tarjeta no lee `Date.now()`, así que no hay diferencia entre servidor y cliente al hidratar.
  assert.match(client, /useState<number \| null>\(null\)/);
  assert.equal(/Date\.now\s*\(/.test(client.slice(client.indexOf("function BundleCard("), client.indexOf("type LoadMode"))), false);
});

test("la tarjeta recibe el reloj del padre y conserva fecha, título y enlace", async () => {
  const source = await read("../bundles-client.tsx");
  const card = source.slice(source.indexOf("function BundleCard("), source.indexOf("type LoadMode"));

  assert.match(card, /now: number \| null/, "el reloj baja como prop");
  assert.match(card, /bundleExpiryView\(item\.expiresAt, now\)/, "la vista sale del módulo puro");
  assert.match(card, /className=\{expiry\.badgeClass\}/);
  assert.match(card, /\{expiry\.label\}/, "el texto del badge se pinta, no solo el color");
  // Lo que ya existía y no se toca.
  assert.match(card, /formatObserved\(item\.expiresAt\)/);
  assert.match(card, /Expira \{expires\}/);
  assert.match(card, /const href = item\.dealUrl \?\? item\.pageUrl \?\? item\.detailsUrl;/);
  assert.match(card, /\{item\.title\}/);
});

test("el padre pasa el reloj y no reordena la lista del servidor", async () => {
  const source = await read("../bundles-client.tsx");
  assert.match(source, /<BundleCard key=\{`\$\{item\.source\}:\$\{item\.bundleKey\}`\} item=\{item\} now=\{now\} \/>/);
  assert.equal(/\.sort\s*\(|\.toSorted\s*\(|\.reverse\s*\(/.test(source), false, "el orden global del servidor se conserva");
});
