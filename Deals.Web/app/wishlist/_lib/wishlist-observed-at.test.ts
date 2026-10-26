import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import {
  OBSERVED_AT_CLOCK_TICK_MS,
  OBSERVED_AT_FRESH_HOURS,
  advanceObservedAtClock,
  observedAtBadge,
  observedAtLabel,
  observedAtStatus
} from "./wishlist-observed-at.ts";

// El distintivo mide la antigüedad del precio observado contra el reloj, así que el «ahora» se inyecta:
// sin eso el borde de las 72 h no se puede fijar y la prueba caduca cada vez que pasa una hora.
const HOUR_MS = 3_600_000;
const NOW = Date.parse("2026-10-18T12:00:00Z");
const ago = (ms: number) => new Date(NOW - ms).toISOString();

test("las etiquetas son ≤72 h, +72 h y Fecha desconocida", () => {
  assert.equal(OBSERVED_AT_FRESH_HOURS, 72);
  assert.equal(observedAtLabel("recent", 72), "≤72 h");
  assert.equal(observedAtLabel("stale", 72), "+72 h");
  assert.equal(observedAtLabel("unknown", 72), "Fecha desconocida");
  // Misma forma que los sellos de proveedor: el número sale del parámetro, no de una constante escondida.
  assert.equal(observedAtLabel("recent", 48), "≤48 h");
  assert.equal(observedAtLabel("stale", 48), "+48 h");
  assert.equal(observedAtBadge(ago(70 * HOUR_MS), NOW)?.label, "≤72 h");
});

test("el borde exacto de 72 h sigue siendo reciente y un milisegundo más ya es antiguo", () => {
  assert.equal(observedAtStatus(ago(72 * HOUR_MS), NOW), "recent");
  assert.equal(observedAtStatus(ago(72 * HOUR_MS + 1), NOW), "stale");
  assert.equal(observedAtBadge(ago(72 * HOUR_MS), NOW)?.label, "≤72 h");
  assert.equal(observedAtBadge(ago(72 * HOUR_MS + 1), NOW)?.label, "+72 h");
  // Un instante idéntico al «ahora» tiene edad 0: no es futuro y no es desconocido.
  assert.equal(observedAtStatus(ago(0), NOW), "recent");
  assert.equal(observedAtStatus(ago(1), NOW), "recent");
});

test("una fecha futura se lee como desconocida, nunca como reciente", () => {
  for (const future of [1, HOUR_MS, 24 * HOUR_MS]) {
    const value = new Date(NOW + future).toISOString();
    assert.equal(observedAtStatus(value, NOW), "unknown", `futuro +${future}ms`);
    assert.equal(observedAtBadge(value, NOW)?.label, "Fecha desconocida");
    assert.equal(observedAtBadge(value, NOW)?.observedAt, null);
  }
});

test("sin fecha, basura o una fecha sin hora se leen como desconocidas sin inventar un precio reciente", () => {
  for (const value of [null, undefined, "", "   ", "ayer", "2026-10-18", 1729000000, {}, []]) {
    assert.equal(observedAtStatus(value, NOW), "unknown", `valor ${String(value)}`);
    const badge = observedAtBadge(value, NOW);
    assert.ok(badge, `hay reloj: siempre hay distintivo para ${String(value)}`);
    assert.equal(badge.status, "unknown");
    assert.equal(badge.label, "Fecha desconocida");
    assert.equal(badge.observedAt, null);
  }
});

test("una fecha inválida del calendario no se convierte en otra fecha", () => {
  // `new Date("2026-02-30T00:00:00Z")` no existe: el render no debe decir «reciente» de un día que no pasó.
  assert.equal(observedAtStatus("2026-02-30T00:00:00Z", Date.parse("2026-03-05T00:00:00Z")), "unknown");
});

test("la fecha se normaliza a UTC conservando el instante, así una zona explícita no mueve el borde", () => {
  // 2026-10-15T13:00:00+02:00 es 2026-10-15T11:00:00Z: 73 h antes de NOW.
  const badge = observedAtBadge("2026-10-15T13:00:00+02:00", NOW);
  assert.ok(badge);
  assert.equal(badge.status, "stale");
  assert.equal(badge.observedAt, "2026-10-15T11:00:00.000Z");
  // El mismo instante escrito en UTC y con un offset opuesto da exactamente el mismo distintivo.
  assert.deepEqual(observedAtBadge("2026-10-15T11:00:00Z", NOW), badge);
  assert.equal(observedAtBadge("2026-10-18T13:00:00+02:00", NOW)?.status, "recent");
});

test("la ventana es un parámetro y no una constante de la pantalla", () => {
  assert.equal(observedAtStatus(ago(40 * HOUR_MS), NOW, 48), "recent");
  assert.equal(observedAtStatus(ago(49 * HOUR_MS), NOW, 48), "stale");
  assert.equal(observedAtBadge(ago(49 * HOUR_MS), NOW, 48)?.label, "+48 h");
  assert.equal(observedAtBadge(ago(40 * HOUR_MS), NOW, 48)?.label, "≤48 h");
});

test("sin reloj montado no hay distintivo: el servidor y la primera hidratación dicen lo mismo", () => {
  // El reloj no existe hasta que el componente monta. Devolver una etiqueta aquí obligaría al servidor a
  // inventarse un «ahora» distinto del del cliente, que es exactamente el desajuste de hidratación que se
  // quiere evitar.
  assert.equal(observedAtBadge("2026-10-18T11:00:00Z", null), null);
  assert.equal(observedAtBadge("2026-10-18T11:00:00Z", Number.NaN), null);
  assert.equal(observedAtBadge("2026-10-18T11:00:00Z", Number.POSITIVE_INFINITY), null);
});

test("un reloj que avanza convierte una fecha futura en pasada y cruza el borde de 72 h", () => {
  const observed = "2026-10-18T12:00:00Z";

  // Con el reloj antes del instante la fecha está en el futuro: desconocida, no reciente.
  assert.equal(observedAtBadge(observed, Date.parse("2026-10-18T11:00:00Z"))?.label, "Fecha desconocida");
  // En cuanto el reloj pasa el instante, el mismo dato se vuelve legible sin cambiar el precio.
  assert.equal(observedAtBadge(observed, Date.parse("2026-10-18T12:01:00Z"))?.label, "≤72 h");
  // Y con el mismo dato, 72 h después del instante exacto, el distintivo cruza el borde solo.
  assert.equal(observedAtBadge(observed, Date.parse("2026-10-21T12:00:00Z"))?.label, "≤72 h");
  assert.equal(observedAtBadge(observed, Date.parse("2026-10-21T12:00:01Z"))?.label, "+72 h");
});

test("el reloj de la página se muestrea cada minuto, no una vez por carga", () => {
  assert.equal(OBSERVED_AT_CLOCK_TICK_MS, 60_000);
  // Un minuto es muy inferior a la ventana: el borde de las 72 h se cruza con el reloj, no con una recarga.
  assert.ok(OBSERVED_AT_CLOCK_TICK_MS < OBSERVED_AT_FRESH_HOURS * HOUR_MS);

  assert.equal(advanceObservedAtClock(null, 1_000), 1_000);
  assert.equal(advanceObservedAtClock(1_000, 61_000), 61_000);
  // Una muestra más vieja (suspensión del equipo, ajuste del reloj del sistema) no hace retroceder el
  // distintivo: un precio no «rejuvenece» porque el reloj dé un salto atrás.
  assert.equal(advanceObservedAtClock(61_000, 1_000), 61_000);
});

test("una muestra de reloj inválida no se guarda: el distintivo se queda con la última buena", () => {
  for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, -1, "ahora"]) {
    assert.equal(advanceObservedAtClock(61_000, bad), 61_000, `muestra ${String(bad)}`);
    assert.equal(advanceObservedAtClock(null, bad), null, `muestra inicial ${String(bad)}`);
  }
});

// El módulo es puro por contrato: `node --test` lo ejecuta sin React, sin el contrato del BFF y sin `Intl`.
test("el módulo no depende de nada, ni de React ni del contrato del BFF", async () => {
  const source = await readFile(new URL("./wishlist-observed-at.ts", import.meta.url), "utf8");
  assert.doesNotMatch(source, /^\s*import\s/m);
  assert.doesNotMatch(source, /require\(/);
});
