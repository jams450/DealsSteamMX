import { normalizeStore } from "./stores.ts";

export const GAME_TIERS = ["S", "A", "B", "C", "D", "F"] as const;
export type GameTier = (typeof GAME_TIERS)[number];

// Una tarjeta es UNA partida reseñada, no un juego: el mismo juego jugado dos veces en el año —incluso en
// la misma plataforma— son dos tarjetas con rango independiente (spec S5). `reviewId` es la llave de
// guardado, arrastre y orden; `gameId` solo ancla el juego canónico para las fusiones.
export type GameTierEntry = {
  readonly reviewId: number;
  readonly gameId: number;
  readonly title: string;
  readonly imageUrl: string | null;
  readonly platform: string;
  readonly isGoty: boolean;
  readonly tier: GameTier | "Unranked";
  readonly position: number;
};

type RecordLike = Record<string, unknown>;

function record(value: unknown): value is RecordLike {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function value(source: RecordLike, key: string): unknown {
  return source[key] ?? source[key[0]!.toUpperCase() + key.slice(1)];
}

function positiveId(input: unknown): number | null {
  return typeof input === "number" && Number.isSafeInteger(input) && input > 0 ? input : null;
}

export function normalizeGameTierEntry(input: unknown): GameTierEntry | null {
  if (!record(input)) return null;
  const reviewId = positiveId(value(input, "reviewId"));
  const gameId = positiveId(value(input, "gameId"));
  const title = value(input, "title");
  const platform = normalizeStore(value(input, "platform"));
  const tier = value(input, "tier");
  const position = value(input, "position");
  const imageUrl = value(input, "imageUrl");
  if (reviewId === null || gameId === null || typeof title !== "string" || !title.trim()) return null;
  if (platform === null || typeof position !== "number" || !Number.isSafeInteger(position) || position < 0) return null;
  if (tier !== "Unranked" && !(GAME_TIERS as readonly unknown[]).includes(tier)) return null;
  // Solo el literal `true` marca GOTY, igual que en el contrato de reseñas.
  const isGoty = value(input, "isGoty") === true;
  const validImage = typeof imageUrl === "string" && /^https:\/\//i.test(imageUrl) ? imageUrl : null;
  return { reviewId, gameId, title: title.trim(), imageUrl: validImage, platform, isGoty, tier: tier as GameTier | "Unranked", position };
}

export function normalizeGameTierList(input: unknown): readonly GameTierEntry[] | null {
  if (!Array.isArray(input)) return null;
  const entries: GameTierEntry[] = [];
  const seen = new Set<number>();
  for (const item of input) {
    const entry = normalizeGameTierEntry(item);
    // El duplicado inválido es el reviewId repetido. Dos tarjetas con el mismo gameId son legítimas.
    if (entry === null || seen.has(entry.reviewId)) return null;
    seen.add(entry.reviewId);
    entries.push(entry);
  }
  return entries;
}

// Guardado: manda la partida reseñada. Un payload que solo trae gameId no dice cuál de dos partidas se
// movió, así que no se acepta.
export function parseGameTierUpdate(input: unknown): { reviewId: number; tier: GameTier; position: number } | null {
  if (!record(input)) return null;
  const reviewId = positiveId(input.reviewId);
  const tier = input.tier;
  const position = input.position;
  if (reviewId === null || !(GAME_TIERS as readonly unknown[]).includes(tier) ||
      typeof position !== "number" || !Number.isSafeInteger(position) || position < 0 || position > 10000) return null;
  return { reviewId, tier: tier as GameTier, position };
}

// Años ofrecidos por el selector, derivados de las reseñas fechadas del usuario. Se deduplican y se
// ordenan del más nuevo al más viejo, para que "el más reciente" sea determinista y no dependa del orden
// en que el servidor los haya consultado.
export function normalizeGameTierYears(input: unknown): readonly number[] | null {
  if (!Array.isArray(input)) return null;
  const years = new Set<number>();
  for (const item of input) {
    if (typeof item !== "number" || !Number.isSafeInteger(item) || item < 1000 || item > 9999) return null;
    years.add(item);
  }
  return [...years].sort((left, right) => right - left);
}
