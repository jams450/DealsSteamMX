import type { GameTier, GameTierEntry } from "../../../../lib/contracts/game-tiers.ts";

// Helpers puros de la tier list. Viven fuera del componente para poder probarlos con `node --test`: aquí
// está la regla de "una tarjeta por partida reseñada" (S5), el orden determinista y el guardia que evita
// que una respuesta o un guardado viejo pise el año que el usuario está viendo.

// Posición de una tarjeta sin clasificar: el servidor manda int.MaxValue y el orden la deja al final.
export const UNRANKED_POSITION = 2147483647;

// Orden estable dentro de un rango: posición y, en empate, reviewId. Nunca el título: dos partidas del
// mismo juego con la misma posición tienen que conservar un orden entre recargas.
export function sortTierEntries(entries: readonly GameTierEntry[]): readonly GameTierEntry[] {
  return [...entries].sort((left, right) => left.position - right.position || left.reviewId - right.reviewId);
}

// Siguiente posición libre del rango destino; solo cuenta las tarjetas de ese rango.
export function nextPosition(entries: readonly GameTierEntry[], tier: GameTier | "Unranked"): number {
  let max = -1;
  for (const entry of entries) {
    if (entry.tier === tier && entry.position > max) max = entry.position;
  }
  return max + 1;
}

// Mueve una tarjeta a un rango. Devuelve null cuando no hay nada que guardar: la tarjeta no existe o ya
// está en ese rango (soltar en el mismo rango no debe reordenar).
export function moveTierEntry(
  entries: readonly GameTierEntry[],
  reviewId: number,
  tier: GameTier | "Unranked"
): { readonly entries: readonly GameTierEntry[]; readonly position: number } | null {
  const entry = entries.find((candidate) => candidate.reviewId === reviewId);
  if (!entry || entry.tier === tier) return null;
  const position = nextPosition(entries, tier);
  return { entries: applyTierPlacement(entries, reviewId, tier, position), position };
}

// Aplica un puesto guardado a exactamente una partida; el resto de las tarjetas no se toca.
export function applyTierPlacement(
  entries: readonly GameTierEntry[],
  reviewId: number,
  tier: GameTier | "Unranked",
  position: number
): readonly GameTierEntry[] {
  return entries.map((entry) => (entry.reviewId === reviewId ? { ...entry, tier, position } : entry));
}

export type VersionGate = {
  begin(): number;
  isCurrent(token: number): boolean;
};

// Guardia de respuestas y guardados: solo el último ticket es vigente, así que una respuesta del año
// anterior que llega tarde no se aplica. Sin estado compartido entre usuarios ni efectos.
export function createVersionGate(): VersionGate {
  let current = 0;
  return {
    begin() {
      current += 1;
      return current;
    },
    isCurrent(token) {
      return token === current;
    }
  };
}
