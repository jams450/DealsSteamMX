type UnknownRecord = Record<string, unknown>;

export type SteamSearchResult = {
  readonly appId: number;
  readonly name: string;
  readonly type: string | null;
  readonly imageUrl: string | null;
};

export type SteamGame = SteamSearchResult & {
  readonly isFree: boolean;
  readonly currency: string | null;
  readonly initialPriceMinor: number | null;
  readonly currentPriceMinor: number | null;
  readonly discountPercent: number | null;
  readonly region: string | null;
  readonly observedAt: string | null;
};

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

function toPositiveInteger(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

function toPriceMinor(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

function toPercent(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isInteger(parsed) && parsed >= 0 && parsed <= 100 ? parsed : null;
}

function toText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function normalizeSearchResult(value: unknown): SteamSearchResult | null {
  if (!isRecord(value)) return null;

  const appId = toPositiveInteger(value.appId ?? value.AppId);
  const name = toText(value.name ?? value.Name);
  if (appId === null || name === null) return null;

  return {
    appId,
    name,
    type: toText(value.type ?? value.Type),
    imageUrl: toText(value.imageUrl ?? value.ImageUrl)
  };
}

export function normalizeSteamSearch(input: unknown): readonly SteamSearchResult[] {
  const items: unknown[] = Array.isArray(input)
    ? input
    : isRecord(input) && Array.isArray(input.results ?? input.Results)
      ? (input.results ?? input.Results) as unknown[]
      : [];

  return items.flatMap((item) => {
    const game = normalizeSearchResult(item);
    return game === null ? [] : [game];
  });
}

export function normalizeSteamGame(input: unknown): SteamGame | null {
  const value = isRecord(input) && isRecord(input.game ?? input.Game) ? input.game ?? input.Game : input;
  const game = normalizeSearchResult(value);
  if (game === null || !isRecord(value)) return null;

  return {
    ...game,
    isFree: value.isFree === true || value.IsFree === true,
    currency: toText(value.currency ?? value.Currency),
    initialPriceMinor: toPriceMinor(value.initialPriceMinor ?? value.InitialPriceMinor),
    currentPriceMinor: toPriceMinor(value.currentPriceMinor ?? value.CurrentPriceMinor),
    discountPercent: toPercent(value.discountPercent ?? value.DiscountPercent),
    region: toText(value.region ?? value.Region),
    observedAt: toText(value.observedAt ?? value.ObservedAt)
  };
}
