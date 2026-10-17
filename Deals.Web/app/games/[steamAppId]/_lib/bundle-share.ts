import type { SteamBundleTier } from "@/lib/contracts/steam";

// Reparto equitativo del precio de un tier entre sus juegos. Módulo puro para poder probarlo con
// `node --test`: una cifra inventada sobre un tier sin precio, addon o incompleto es exactamente el
// error que no falla, solo muestra un número falso. Nada de aquí es una oferta ni entra en el
// comparador de precios.

export type TierShareStatus =
  | "ok"
  | "addon"
  | "items_incomplete"
  | "no_tier_price"
  | "currency_unknown"
  | "no_eligible_items";

export type TierShare = {
  readonly status: TierShareStatus;
  readonly priceMinor: number | null;
  readonly currency: string | null;
  readonly eligibleGameCount: number;
  readonly itemCount: number;
  readonly allocatedPerGameMinor: number | null;
  readonly requiresWholeTierPurchase: boolean;
};

// Tri-estado explícito de la pertenencia del juego a un tier: `true`/`false` solo cuando la identidad
// del ítem lo prueba (`tierMembership`), y `"unknown"` cuando el snapshot no trae ids. `"unknown"`
// obliga a abstenerse: nunca se lee como una ausencia.
export type TierMembership = true | false | "unknown";

export type StandaloneComparison = {
  readonly cheaper: boolean;
  readonly allocatedPerGameMinor: number;
  readonly currency: string;
};

const COMPARABLE_ITEM_TYPE = "game";

function isGameItem(type: string | null): boolean {
  return type !== null && type.toLowerCase() === COMPARABLE_ITEM_TYPE;
}

export function tierShare(tier: SteamBundleTier): TierShare {
  const eligibleGameCount = tier.games.filter((item) => isGameItem(item.type)).length;

  let status: TierShareStatus;
  let allocatedPerGameMinor: number | null;
  if (!tier.itemsComplete) {
    status = "items_incomplete";
    allocatedPerGameMinor = null;
  } else if (tier.addon) {
    status = "addon";
    allocatedPerGameMinor = null;
  } else if (tier.priceMinor === null) {
    status = "no_tier_price";
    allocatedPerGameMinor = null;
  } else if (tier.currency === null || tier.currency.trim() === "") {
    status = "currency_unknown";
    allocatedPerGameMinor = null;
  } else if (eligibleGameCount === 0) {
    status = "no_eligible_items";
    allocatedPerGameMinor = null;
  } else {
    status = "ok";
    allocatedPerGameMinor = Math.round(tier.priceMinor / eligibleGameCount);
  }

  return {
    status,
    priceMinor: tier.priceMinor,
    currency: tier.currency,
    eligibleGameCount,
    itemCount: tier.games.length,
    allocatedPerGameMinor,
    requiresWholeTierPurchase: allocatedPerGameMinor !== null
  };
}

/**
 * Compara el reparto de un tier contra el precio suelto del juego, solo cuando el reparto es asignable,
 * la moneda coincide y la pertenencia del juego a ese tier es un hecho (`tierContainsGame === true`).
 * Sin identidad de ítem devuelve `null`: no se adivina por título ni por parecido.
 *
 * La comparación usa la división exacta (`priceMinor / count < standalone` es
 * `priceMinor < standalone * count`), nunca la cifra redondeada que se muestra, para que el redondeo no
 * invente ni esconda un ahorro.
 */
export function compareShareToStandalone(
  share: TierShare,
  standaloneMinor: number | null,
  standaloneCurrency: string | null,
  tierContainsGame: TierMembership
): StandaloneComparison | null {
  if (tierContainsGame !== true) return null;
  if (share.status !== "ok" || share.priceMinor === null || share.currency === null) return null;
  if (share.allocatedPerGameMinor === null || share.eligibleGameCount <= 0) return null;
  if (standaloneMinor === null || standaloneMinor < 0) return null;
  if (standaloneCurrency === null) return null;
  if (share.currency.toUpperCase() !== standaloneCurrency.toUpperCase()) return null;

  return {
    cheaper: share.priceMinor < standaloneMinor * share.eligibleGameCount,
    allocatedPerGameMinor: share.allocatedPerGameMinor,
    currency: share.currency
  };
}

/**
 * Prueba, por identidad exacta y nunca por título, si un tier lista el juego consultado. Solo un id de
 * ítem igual a `gameItadId` prueba pertenencia. Una ausencia se prueba únicamente cuando **todos** los
 * ítems traen id: si falta aunque sea uno, ese ítem desconocido puede ser el juego, así que se
 * abstiene (`"unknown"`). Un `gameItadId` vacío o un tier sin ítems también se abstiene; un snapshot
 * viejo sin ids cae en este mismo caso y nunca se lee como «este tier no lo incluye».
 */
export function tierMembership(tier: SteamBundleTier, gameItadId: string | null): TierMembership {
  const wanted = gameItadId ?? "";
  if (wanted === "") return "unknown";

  const items = tier.games;
  if (items.length === 0) return "unknown";
  if (items.some((item) => item.itadGameId === wanted)) return true;

  const allIdentified = items.every((item) => item.itadGameId !== null && item.itadGameId !== "");
  return allIdentified ? false : "unknown";
}

/**
 * Comparación por juego lista para el detalle: reparto del tier contra el precio suelto, solo para el
 * tier que realmente lista el juego y en la misma moneda. Devuelve `null` cuando no hay afirmación
 * honesta posible (tier equivocado, identidad ausente o moneda distinta).
 */
export function gameTierComparison(
  tier: SteamBundleTier,
  standaloneMinor: number | null,
  standaloneCurrency: string | null,
  gameItadId: string | null
): StandaloneComparison | null {
  return compareShareToStandalone(
    tierShare(tier),
    standaloneMinor,
    standaloneCurrency,
    tierMembership(tier, gameItadId)
  );
}
