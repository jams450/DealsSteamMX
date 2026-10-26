// Antigüedad del precio observado de una banda (oficial / keyshop) de la wishlist. Sin imports a propósito:
// `node --test` ejecuta este archivo directamente y el módulo no arrastra React, `Intl` ni el contrato del BFF.
//
// No reutiliza `freshnessShortStatusLabel` (`lib/contracts/catalog-freshness.ts`) por una razón concreta:
// allí el estado sin fecha significa «el juego no tiene precio» y se escribe «Sin precio». Aquí el precio
// existe —solo falta cuándo se leyó— y decir «Sin precio» junto a un importe sería mentir sobre la fila.
// El borde también es distinto: esa ventana la fija el servidor, esta mide el reloj del cliente.

// Ventana del distintivo: un precio leído hace 72 h o menos sigue siendo reciente. Mismo número que la
// ventana del catálogo (S6), pero se pasa como parámetro para que el panel y la etiqueta no se desalineen.
export const OBSERVED_AT_FRESH_HOURS = 72;

// Periodo con el que la página vuelve a leer el reloj. Un minuto es muy inferior a la ventana: así una fecha
// futura se vuelve pasada y el borde de las 72 h se cruza con el reloj, no con una recarga.
export const OBSERVED_AT_CLOCK_TICK_MS = 60_000;

const HOUR_MS = 3_600_000;

export type ObservedAtStatus = "recent" | "stale" | "unknown";

export interface ObservedAtBadge {
  readonly status: ObservedAtStatus;
  // Texto corto del distintivo: «≤72 h», «+72 h» o «Fecha desconocida».
  readonly label: string;
  // Instante normalizado a UTC (`toISOString`), o null cuando la fecha falta o no es utilizable.
  readonly observedAt: string | null;
}

// Un `Date.parse` sobre cualquier texto acepta cosas que no son un instante (una fecha sin hora se lee como
// medianoche UTC) y normaliza días fuera de calendario (`2026-02-30` rueda a marzo). Se exige la forma
// `fechaThora…` y se compara el día contra el que el texto declara. Un número epoch no se acepta: la API manda
// texto ISO y aceptar números abriría el rango de valores que llegan al render.
function toObservedAtMs(value: unknown): number | null {
  if (typeof value !== "string") return null;
  const text = value.trim();
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(text)) return null;

  const day = text.slice(0, 10);
  const parsed = Date.parse(text);
  const dayMs = Date.parse(`${day}T00:00:00Z`);
  if (!Number.isFinite(parsed) || !Number.isFinite(dayMs)) return null;
  // El día se valida por separado y en UTC a medianoche: así un offset (`-05:00`) no se confunde con un
  // desbordamiento de calendario.
  if (new Date(dayMs).toISOString().slice(0, 10) !== day) return null;

  return parsed;
}

// Reciente si la edad es de 72 h o menos (el borde exacto incluido), antiguo si es mayor y desconocida si no
// hay fecha o si la fecha está en el futuro — un reloj adelantado no convierte un precio en recién leído.
export function observedAtStatus(
  observedAt: unknown,
  nowMs: number,
  hours: number = OBSERVED_AT_FRESH_HOURS
): ObservedAtStatus {
  const observedMs = toObservedAtMs(observedAt);
  if (observedMs === null || !Number.isFinite(nowMs)) return "unknown";

  const ageMs = nowMs - observedMs;
  if (ageMs < 0) return "unknown";
  return ageMs <= hours * HOUR_MS ? "recent" : "stale";
}

export function observedAtLabel(status: ObservedAtStatus, hours: number = OBSERVED_AT_FRESH_HOURS): string {
  switch (status) {
    case "recent":
      return `≤${hours} h`;
    case "stale":
      return `+${hours} h`;
    default:
      return "Fecha desconocida";
  }
}

// Todo lo que el render necesita del distintivo en una sola llamada: estado, texto y el instante en UTC que
// el tooltip muestra completo.
//
// `nowMs` puede ser null: la página no tiene reloj hasta que el componente monta. Devolver null en ese caso es
// deliberado —el servidor no inventa un «ahora» que el cliente no comparte, así que el HTML del servidor y la
// primera hidratación coinciden en «todavía sin distintivo» y el texto aparece en la primera muestra del efecto—.
// Con reloj y estado «desconocida» el instante también es null: si la fecha está en el futuro no se puede
// afirmar cuándo se leyó el precio, y enseñar ese instante como si fuera un hecho sería peor que decir que la
// fecha no se conoce.
export function observedAtBadge(
  observedAt: unknown,
  nowMs: number | null,
  hours: number = OBSERVED_AT_FRESH_HOURS
): ObservedAtBadge | null {
  if (nowMs === null || !Number.isFinite(nowMs)) return null;

  const status = observedAtStatus(observedAt, nowMs, hours);
  const observedMs = status === "unknown" ? null : toObservedAtMs(observedAt);
  return {
    status,
    label: observedAtLabel(status, hours),
    observedAt: observedMs === null ? null : new Date(observedMs).toISOString()
  };
}

// Siguiente valor del reloj de la página. Nunca retrocede ni acepta una muestra inválida: una suspensión del
// equipo o un ajuste del reloj del sistema no deben rejuvenecer un precio hasta volver a decir «≤72 h».
export function advanceObservedAtClock(current: number | null, sample: unknown): number | null {
  if (typeof sample !== "number" || !Number.isFinite(sample) || sample < 0) return current;
  if (current !== null && sample < current) return current;
  return sample;
}
