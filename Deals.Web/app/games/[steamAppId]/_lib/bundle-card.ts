import type { SteamBundleTier, SteamGameBundle } from "@/lib/contracts/steam";

/**
 * El bundle más barato **con precio** que incluye este juego, para la tarjeta del resumen. Devuelve
 * `null` si no hay bundle, o si el proveedor no publicó precio en ninguno de sus tiers: «Sin precio en
 * ITAD» no es una cifra y la tarjeta no puede inventarla.
 *
 * Se prefiere un tier en la moneda de comparación (`comparisonCurrency`, MXN en este proyecto). El filtro
 * no es cosmético: los `priceMinor` vienen en la moneda del tier, así que comparar 1999 (MXN) contra 1999
 * (USD) elegiría el equivocado. Si el proveedor solo publica en otra moneda, se devuelve esa, sin convertir.
 *
 * Vive aquí y no en el componente porque el componente es `.tsx` y no se puede cargar con `node --test`:
 * una elección de precio sin chequeo es justo el tipo de error que no falla, solo muestra otra cifra.
 */
export type PricedBundleTier = {
  readonly bundle: SteamGameBundle;
  readonly tier: SteamBundleTier;
  /** No nulos: el filtro de arriba los descarta antes de construir el candidato. */
  readonly priceMinor: number;
  readonly currency: string;
};

export function pickPricedBundleTier(
  bundles: readonly SteamGameBundle[],
  comparisonCurrency: string
): PricedBundleTier | null {
  const priced: PricedBundleTier[] = [];
  for (const bundle of bundles) {
    for (const tier of bundle.tiers) {
      if (tier.priceMinor === null || tier.currency === null) continue;
      priced.push({ bundle, tier, priceMinor: tier.priceMinor, currency: tier.currency });
    }
  }
  if (priced.length === 0) return null;

  const comparable = priced.filter(
    (candidate) => candidate.currency.toUpperCase() === comparisonCurrency.toUpperCase()
  );
  const pool = comparable.length > 0 ? comparable : priced;

  let lowest = pool[0];
  for (const candidate of pool) {
    if (candidate.priceMinor < lowest.priceMinor) lowest = candidate;
  }
  return lowest;
}
