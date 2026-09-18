// Métricas puras de la wishlist. Sin imports a propósito: `node --test` puede ejecutar este archivo
// directamente (Node v26 quita los tipos) y el módulo no arrastra React ni el contrato del BFF.

export const SCORE_MAX = 10;
export const DISCOUNT_WEIGHT = 7;
export const LOW_WEIGHT = 3;
// A partir de este descuento la parte de escala se considera completa.
export const SCORE_CEILING = 90;

export function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

export function round1(value: number): number {
  return Math.round(value * 10) / 10;
}

// El descuento solo tiene sentido contra un precio base en MXN: `bestMinor` siempre llega en MXN y
// mezclar monedas daría un porcentaje inventado. `bestMinor === 0` es válido (juego gratis).
// Un mínimo por encima del base devuelve un porcentaje negativo, y está bien: la UI lo pinta como "+".
export function discountPercent(
  basePriceMinor: number | null,
  baseCurrency: string | null,
  bestMinor: number | null
): number | null {
  if (baseCurrency !== "MXN") return null;
  if (basePriceMinor === null || !Number.isFinite(basePriceMinor) || basePriceMinor <= 0) return null;
  if (bestMinor === null || !Number.isFinite(bestMinor)) return null;
  return round1(((basePriceMinor - bestMinor) / basePriceMinor) * 100);
}

export interface DealScoreInput {
  readonly basePriceMinor: number | null;
  readonly baseCurrency: string | null;
  readonly bestMinor: number | null;
  readonly historyLowMinor: number | null;
  readonly historyLowCurrency: string | null;
  readonly minViableDiscountPercent: number;
}

// Score híbrido 0-10:
//   - 7 puntos por la escala de descuento frente al mínimo viable (0 en el umbral, 1 a >=90%).
//   - 3 puntos por cercanía al mínimo histórico (1 cuando el mejor precio ya está en o por debajo).
// Por debajo del umbral el descuento aporta 0, pero la cercanía al mínimo histórico sigue sumando
// hasta 3: un juego sin descuento suficiente puede seguir siendo una buena compra.
export function dealScore(input: DealScoreInput): number | null {
  const disc = discountPercent(input.basePriceMinor, input.baseCurrency, input.bestMinor);
  if (disc === null) return null;

  const threshold = Number.isFinite(input.minViableDiscountPercent)
    ? clamp(input.minViableDiscountPercent, 0, 95)
    : 50;
  const ceiling = SCORE_CEILING;
  const discountFactor =
    threshold >= ceiling ? (disc >= threshold ? 1 : 0) : clamp((disc - threshold) / (ceiling - threshold), 0, 1);

  const best = input.bestMinor;
  const lowFactor =
    input.historyLowCurrency === "MXN" && input.historyLowMinor !== null && input.historyLowMinor > 0 && best !== null
      ? clamp(input.historyLowMinor / best, 0, 1)
      : 0;

  return round1(DISCOUNT_WEIGHT * discountFactor + LOW_WEIGHT * lowFactor);
}
