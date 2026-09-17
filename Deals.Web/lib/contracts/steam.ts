type UnknownRecord = Record<string, unknown>;

export type SteamSearchResult = {
  readonly appId: number;
  readonly name: string;
  readonly type: string | null;
  readonly imageUrl: string | null;
};

export const STEAM_OFFER_CLASSIFICATIONS = ["official", "authorized"] as const;
export type SteamOfferClassification = (typeof STEAM_OFFER_CLASSIFICATIONS)[number];

export const STEAM_PRICING_TYPES = ["regional", "fx_estimate", "unconverted"] as const;
export type SteamPricingType = (typeof STEAM_PRICING_TYPES)[number];

export type SteamGameOffer = {
  readonly source: string;
  readonly offerKey: string;
  readonly shopId: string | null;
  readonly shopName: string;
  readonly classification: SteamOfferClassification;
  readonly drmNames: readonly string[];
  readonly platformNames: readonly string[];
  readonly originalCurrency: string;
  readonly originalRegularPriceMinor: number | null;
  readonly originalCurrentPriceMinor: number | null;
  readonly mxnRegularPriceMinor: number | null;
  readonly mxnCurrentPriceMinor: number | null;
  readonly fxRate: number | null;
  readonly fxRateDate: string | null;
  readonly fxSource: string | null;
  readonly pricingType: SteamPricingType;
  readonly discountPercent: number | null;
  readonly dealUrl: string | null;
  readonly observedAt: string | null;
};

export type SteamGame = SteamSearchResult & {
  readonly isFree: boolean;
  readonly currency: string | null;
  readonly initialPriceMinor: number | null;
  readonly currentPriceMinor: number | null;
  readonly discountPercent: number | null;
  readonly lowestPriceMinor: number | null;
  readonly lowestPriceAt: string | null;
  readonly region: string | null;
  readonly observedAt: string | null;
  readonly offers: readonly SteamGameOffer[];
  readonly offersRefreshedAt: string | null;
  readonly offersStale: boolean;
};

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

function read(value: UnknownRecord, key: string): unknown {
  return value[key] ?? value[key.charAt(0).toUpperCase() + key.slice(1)];
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

function toCurrencyCode(value: unknown): string | null {
  const text = toText(value)?.toUpperCase();
  return text !== undefined && /^[A-Z]{3}$/.test(text) ? text : null;
}

function toClassification(value: unknown): SteamOfferClassification | null {
  const text = toText(value)?.toLowerCase();
  return text === "official" || text === "authorized" ? text : null;
}

function toPricingType(value: unknown): SteamPricingType | null {
  const text = toText(value)?.toLowerCase();
  return text === "regional" || text === "fx_estimate" || text === "unconverted" ? text : null;
}

function toPositiveDecimal(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

// Listas de nombres del proveedor (DRM/plataformas): se acotan para que un payload abusivo no
// infle la respuesta ni el render.
const MAX_OFFER_NAME_ITEMS = 20;
const MAX_OFFER_NAME_LENGTH = 80;

function toNameList(value: unknown): readonly string[] {
  if (!Array.isArray(value)) return [];
  const seen = new Set<string>();
  const names: string[] = [];
  for (const entry of value) {
    const name = toText(entry);
    if (name === null) continue;
    const bounded = name.slice(0, MAX_OFFER_NAME_LENGTH);
    const key = bounded.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    names.push(bounded);
    if (names.length === MAX_OFFER_NAME_ITEMS) break;
  }
  return names;
}

function toIsoDate(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !/^\d{4}-\d{2}-\d{2}$/.test(text)) return null;
  const date = new Date(`${text}T00:00:00Z`);
  return Number.isNaN(date.getTime()) || date.toISOString().slice(0, 10) !== text ? null : text;
}

// Solo ISO-8601 con hora y zona: cualquier otra cosa se descarta (null) en vez de llegar al render.
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

function toIsoDateTime(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !ISO_DATE_TIME.test(text)) return null;
  // El regex no valida el calendario: "2026-02-30" se convierte en marzo, así que se revisa el día.
  if (toIsoDate(text.slice(0, 10)) === null) return null;
  return Number.isNaN(new Date(text).getTime()) ? null : text;
}

// Enlace externo: solo https absoluto. Se devuelve el string tal cual (sin trim) para no
// alterar el tag de afiliado de ITAD; cualquier otra cosa es null.
function toHttpsUrl(value: unknown): string | null {
  if (typeof value !== "string" || !value) return null;
  try {
    return new URL(value).protocol === "https:" ? value : null;
  } catch {
    return null;
  }
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

function normalizeOffer(value: unknown): SteamGameOffer | null {
  if (!isRecord(value)) return null;

  const source = toText(read(value, "source"));
  const offerKey = toText(read(value, "offerKey"));
  const shopName = toText(read(value, "shopName"));
  const classification = toClassification(read(value, "classification"));
  const originalCurrency = toCurrencyCode(read(value, "originalCurrency"));
  const pricingType = toPricingType(read(value, "pricingType"));
  if (
    source === null ||
    offerKey === null ||
    shopName === null ||
    classification === null ||
    originalCurrency === null ||
    pricingType === null
  ) {
    return null;
  }

  return {
    source,
    offerKey,
    shopId: toText(read(value, "shopId")),
    shopName,
    classification,
    drmNames: toNameList(read(value, "drmNames")),
    platformNames: toNameList(read(value, "platformNames")),
    originalCurrency,
    originalRegularPriceMinor: toPriceMinor(read(value, "originalRegularPriceMinor")),
    originalCurrentPriceMinor: toPriceMinor(read(value, "originalCurrentPriceMinor")),
    mxnRegularPriceMinor: toPriceMinor(read(value, "mxnRegularPriceMinor")),
    mxnCurrentPriceMinor: toPriceMinor(read(value, "mxnCurrentPriceMinor")),
    fxRate: toPositiveDecimal(read(value, "fxRate")),
    fxRateDate: toIsoDate(read(value, "fxRateDate")),
    fxSource: toText(read(value, "fxSource")),
    pricingType,
    discountPercent: toPercent(read(value, "discountPercent")),
    dealUrl: toHttpsUrl(read(value, "dealUrl")),
    observedAt: toIsoDateTime(read(value, "observedAt"))
  };
}

export function normalizeSteamOffers(input: unknown): readonly SteamGameOffer[] {
  return Array.isArray(input)
    ? input.flatMap((item) => {
        const offer = normalizeOffer(item);
        return offer === null ? [] : [offer];
      })
    : [];
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
    lowestPriceMinor: toPriceMinor(value.lowestPriceMinor ?? value.LowestPriceMinor),
    lowestPriceAt: toIsoDateTime(value.lowestPriceAt ?? value.LowestPriceAt),
    region: toText(value.region ?? value.Region),
    observedAt: toIsoDateTime(value.observedAt ?? value.ObservedAt),
    offers: normalizeSteamOffers(read(value, "offers")),
    offersRefreshedAt: toIsoDateTime(read(value, "offersRefreshedAt")),
    offersStale: read(value, "offersStale") === true
  };
}
