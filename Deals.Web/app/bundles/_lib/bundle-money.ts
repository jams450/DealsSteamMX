import type { BundleCatalogTier, BundleMoney } from "@/lib/contracts/bundles";

// Valores USD/MXN del catálogo de bundles. Módulo puro para poder probarlo con `node --test` y para que
// la tarjeta no haga aritmética de tipo de cambio: el servidor ya convirtió con la tasa persistida y aquí
// solo se decide qué par se pinta, cuál es estimación (`≈`) y qué metadatos la respaldan. Una cifra que
// el servidor no mandó queda en "no disponible", nunca en 0.

export const DUAL_CURRENCIES = ["USD", "MXN"] as const;

export type DualCurrency = (typeof DUAL_CURRENCIES)[number];

export type MoneyLeg = {
  readonly currency: DualCurrency;
  /** Importe en unidades menores tal como llegó del servidor. `null` = sin conversión disponible. */
  readonly amountMinor: number | null;
  /** `true` cuando la cifra es una conversión y por tanto se pinta con `≈`. */
  readonly approximate: boolean;
  /** `true` cuando es la moneda nativa del tier: ya se ve en la línea de precio original. */
  readonly isNative: boolean;
};

export type FxDisclosure = {
  /**
   * Tasa de la fila persistida que usó el servidor, en la dirección que fija la moneda nativa del tier:
   * USD nativo ⇒ MXN por USD (p. ej. 18.2); MXN nativo ⇒ fila MXN/USD explícita (p. ej. 0.055 USD por
   * MXN). Aquí no se invierte nada: el servidor nunca sintetiza la inversa y el navegador tampoco.
   */
  readonly rate: number | null;
  readonly rateDate: string | null;
  readonly source: string | null;
};

export type DualMoneyView = {
  readonly nativeCurrency: string | null;
  readonly legs: readonly MoneyLeg[];
  /** Las que la tarjeta pinta: nunca la nativa, para no repetir la misma moneda dos veces. */
  readonly displayLegs: readonly MoneyLeg[];
  readonly fx: FxDisclosure | null;
  /** Hay al menos una cifra convertida en pantalla; sin cifra no se anuncia una conversión. */
  readonly hasApproximation: boolean;
};

function hasAnyAmount(money: BundleMoney | null): money is BundleMoney {
  return money !== null && (money.usdMinor !== null || money.mxnMinor !== null);
}

function fxDisclosure(money: BundleMoney): FxDisclosure | null {
  if (money.fxRate === null && money.fxRateDate === null && money.fxSource === null) return null;
  return { rate: money.fxRate, rateDate: money.fxRateDate, source: money.fxSource };
}

// La moneda nativa es la del precio publicado y decide cuál de las dos cifras es la publicada. Si es una
// tercera moneda, ninguna de las dos es nativa y ambas se leen como conversión: se prefiere marcar de más
// a presentar una estimación como si fuera el precio publicado.
function buildView(
  nativeMinor: number | null,
  nativeCurrency: string | null,
  money: BundleMoney | null
): DualMoneyView | null {
  // Sin precio publicado no hay nada que convertir: ni filas ni un cero de relleno.
  if (nativeMinor === null || !hasAnyAmount(money)) return null;

  // Sin moneda nativa la propia línea de precio dice «Sin precio en ITAD»: unas filas USD/MXN al lado
  // se leerían como el precio que la tarjeta acaba de negar.
  const normalizedNative = nativeCurrency?.trim().toUpperCase() ?? "";
  if (normalizedNative === "") return null;
  const legs: MoneyLeg[] = DUAL_CURRENCIES.map((currency) => {
    const isNative = normalizedNative === currency;
    return {
      currency,
      amountMinor: currency === "USD" ? money.usdMinor : money.mxnMinor,
      approximate: !isNative,
      isNative
    };
  });

  const displayLegs = legs.filter((leg) => !leg.isNative);
  const hasApproximation = displayLegs.some((leg) => leg.approximate && leg.amountMinor !== null);

  return {
    nativeCurrency: normalizedNative,
    legs,
    displayLegs,
    fx: hasApproximation ? fxDisclosure(money) : null,
    hasApproximation
  };
}

/** Valores duales del precio del tier. `null` si no hay precio nativo o no hay ninguna cifra dual. */
export function tierMoneyView(tier: BundleCatalogTier): DualMoneyView | null {
  return buildView(tier.priceMinor, tier.currency, tier.priceMoney);
}

/**
 * Valores duales del reparto equitativo por juego. Exige el mismo estado asignable que la cifra nativa
 * (`ok` + importe) y su propio bloque: el reparto convertido nunca se deriva del precio del tier.
 */
export function allocatedPerGameMoneyView(tier: BundleCatalogTier): DualMoneyView | null {
  if (tier.allocationStatus !== "ok") return null;
  return buildView(tier.allocatedPerGameMinor, tier.currency, tier.allocatedPerGameMoney);
}

// Al menos dos decimales y hasta cuatro, sin depender del locale del servidor ni del navegador.
function formatRate(rate: number): string {
  const [integer, fraction = ""] = String(Math.round(rate * 10_000) / 10_000).split(".");
  return `${integer}.${fraction.padEnd(2, "0")}`;
}

// La fecha de tasa llega como YYYY-MM-DD; se arma a mano para no correrla por zona horaria.
function formatRateDate(value: string | null): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? "");
  return match ? `${match[3]}/${match[2]}/${match[1]}` : null;
}

// La dirección de la tasa la determina la moneda nativa del tier. Una tercera moneda no permite saber
// cómo está expresada la fila, así que no se le pone etiqueta (ver `fxDisclosureText`).
function rateQuote(nativeCurrency: string | null): string | null {
  const currency = nativeCurrency === null ? null : nativeCurrency.trim().toUpperCase();
  if (currency === "USD") return "MXN por USD";
  if (currency === "MXN") return "USD por MXN";
  return null;
}

/**
 * Línea que respalda las cifras con `≈`: tasa, fecha y fuente, omitiendo lo que falte. Devuelve `null`
 * cuando no hay nada que declarar, para no pintar una etiqueta vacía.
 *
 * `fxRate` es la fila persistida explícita que usó el servidor, no un par fijo: un tier publicado en USD
 * trae MXN por USD (18.2) y uno publicado en MXN trae la fila MXN/USD (0.055 USD por MXN). Se etiqueta
 * con `nativeCurrency` y jamás se invierte. Con una moneda nativa que no es USD ni MXN la dirección es
 * desconocida: se omite la cifra de la tasa —no la fecha ni la fuente— antes que presentarla al revés.
 */
export function fxDisclosureText(fx: FxDisclosure | null, nativeCurrency: string | null): string | null {
  if (fx === null) return null;

  const parts: string[] = [];
  const quote = rateQuote(nativeCurrency);
  if (fx.rate !== null && quote !== null) parts.push(`Tasa ${formatRate(fx.rate)} ${quote}`);
  const date = formatRateDate(fx.rateDate);
  if (date !== null) parts.push(date);
  if (fx.source !== null) parts.push(fx.source);

  return parts.length > 0 ? parts.join(" · ") : null;
}
