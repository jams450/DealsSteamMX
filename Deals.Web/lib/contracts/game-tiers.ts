export const GAME_TIERS = ["S", "A", "B", "C", "D", "F"] as const;
export type GameTier = (typeof GAME_TIERS)[number];

export type GameTierEntry = {
  readonly gameId: number;
  readonly title: string;
  readonly imageUrl: string | null;
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
  const gameId = positiveId(value(input, "gameId"));
  const title = value(input, "title");
  const tier = value(input, "tier");
  const position = value(input, "position");
  const imageUrl = value(input, "imageUrl");
  if (gameId === null || typeof title !== "string" || !title.trim()) return null;
  if (tier !== "Unranked" && !(GAME_TIERS as readonly unknown[]).includes(tier)) return null;
  if (typeof position !== "number" || !Number.isSafeInteger(position) || position < 0) return null;
  const validImage = typeof imageUrl === "string" && /^https:\/\//i.test(imageUrl) ? imageUrl : null;
  return { gameId, title: title.trim(), imageUrl: validImage, tier: tier as GameTier | "Unranked", position };
}

export function normalizeGameTierList(input: unknown): readonly GameTierEntry[] | null {
  if (!Array.isArray(input)) return null;
  const entries: GameTierEntry[] = [];
  const seen = new Set<number>();
  for (const item of input) {
    const entry = normalizeGameTierEntry(item);
    if (entry === null || seen.has(entry.gameId)) return null;
    seen.add(entry.gameId);
    entries.push(entry);
  }
  return entries;
}

export function parseGameTierUpdate(input: unknown): { gameId: number; tier: GameTier; position: number } | null {
  if (!record(input)) return null;
  const gameId = positiveId(input.gameId);
  const tier = input.tier;
  const position = input.position;
  if (gameId === null || !(GAME_TIERS as readonly unknown[]).includes(tier) ||
      typeof position !== "number" || !Number.isSafeInteger(position) || position < 0 || position > 10000) return null;
  return { gameId, tier: tier as GameTier, position };
}
