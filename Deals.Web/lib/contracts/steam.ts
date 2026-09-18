type UnknownRecord = Record<string, unknown>;

export type SteamSearchResult = {
  readonly appId: number;
  readonly name: string;
  readonly type: string | null;
  readonly imageUrl: string | null;
  readonly hasDetails: boolean;
  readonly refreshedAt: string | null;
};

export const STEAM_OFFER_CLASSIFICATIONS = ["official", "authorized", "keyshop"] as const;
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
  // Mínimo histórico del proveedor, en su propia moneda. Genérico: lo puebla ITAD y gg.deals.
  readonly historyLowAllMinor: number | null;
  readonly historyLowCurrency: string | null;
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

// Bundle externo (V1.1: ITAD). No es una oferta: tiene tiers, varios ítems y caduca. El ahorro se derive
// en el backend por tier contra el precio actual de ITAD, nunca contra Steam ni gg.deals: el cliente
// solo valida la forma y los caps. `status`/`reason` son códigos de máquina; el copy va en la UI.
export type SteamBundleTierItem = {
  readonly title: string;
  readonly type: string | null;
  readonly priceMinor: number | null;
  readonly priceCurrency: string | null;
};

export type SteamBundleTier = {
  readonly priceMinor: number | null;
  readonly currency: string | null;
  readonly addon: boolean;
  readonly itemsComplete: boolean;
  readonly games: readonly SteamBundleTierItem[];
  readonly status: string | null;
  readonly reason: string | null;
  readonly individualTotalMinor: number | null;
  readonly bundlePriceMinor: number | null;
  readonly savingsMinor: number | null;
  readonly savingsPercent: number | null;
  readonly fxRate: number | null;
  readonly fxRateDate: string | null;
  readonly fxSource: string | null;
  readonly pricingType: string | null;
  readonly mxnIndividualTotalMinor: number | null;
  readonly mxnSavingsMinor: number | null;
};

export type SteamGameBundle = {
  readonly bundleKey: string | null;
  readonly title: string;
  readonly shopName: string | null;
  readonly pageUrl: string | null;
  readonly dealUrl: string | null;
  readonly details: string | null;
  readonly publishedAt: string | null;
  readonly expiresAt: string | null;
  readonly observedAt: string | null;
  readonly tiers: readonly SteamBundleTier[];
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
  readonly ggDealsRefreshedAt: string | null;
  readonly ggDealsStale: boolean;
  readonly bundles: readonly SteamGameBundle[];
  readonly bundlesRefreshedAt: string | null;
  readonly bundlesStale: boolean;
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

// Un ahorro puede ser negativo (el bundle cuesta más que sus piezas): sin cifra, no maquillada.
function toSignedMinor(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

// Igual que toPriceMinor pero aceptando negativos: `mxnSavingsMinor` deriva de un ahorro con signo.
function toSignedPercent(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isSafeInteger(parsed) && parsed >= -100 && parsed <= 100 ? parsed : null;
}

function toPercent(value: unknown): number | null {
  const parsed = typeof value === "number" ? value : Number(value);
  return Number.isInteger(parsed) && parsed >= 0 && parsed <= 100 ? parsed : null;
}

function toText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

// Igual que `toText`, pero acotado: los textos de bundle (título, tienda, detalle) son de forma libre
// y no deben inflar la respuesta ni el render.
function toBoundedText(value: unknown, maxLength: number): string | null {
  const text = toText(value);
  return text === null ? null : text.slice(0, maxLength);
}

function toCurrencyCode(value: unknown): string | null {
  const text = toText(value)?.toUpperCase();
  return text !== undefined && /^[A-Z]{3}$/.test(text) ? text : null;
}

function toClassification(value: unknown): SteamOfferClassification | null {
  const text = toText(value)?.toLowerCase();
  // Toda clasificación nueva debe añadirse aquí **y** al array de arriba: `normalizeOffer` descarta
  // (null) la oferta entera cuando esto no valida, así que una clasificación sin soporte desaparece
  // en silencio, sin error de build y sin fallo visible en la UI.
  return text === "official" || text === "authorized" || text === "keyshop" ? text : null;
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
    imageUrl: toText(value.imageUrl ?? value.ImageUrl),
    hasDetails: (value.hasDetails ?? value.HasDetails) === true,
    refreshedAt: toIsoDateTime(value.refreshedAt ?? value.RefreshedAt)
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
    historyLowAllMinor: toPriceMinor(read(value, "historyLowAllMinor")),
    historyLowCurrency: toCurrencyCode(read(value, "historyLowCurrency")),
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

// Un bundle con tier ilegible se conserva: el tier se descarta, no el bundle. Se acotan tiers e ítems
// para que un payload abusivo no infle la respuesta.
const MAX_BUNDLES = 25;
const MAX_BUNDLE_TIERS = 20;
const MAX_BUNDLE_TIER_GAMES = 50;
const MAX_BUNDLE_TITLE_LENGTH = 200;
const MAX_BUNDLE_SHOP_NAME_LENGTH = 120;
const MAX_BUNDLE_DETAILS_LENGTH = 600;
const MAX_BUNDLE_STATUS_LENGTH = 40;
const STEAM_BUNDLE_STATUS_LENGTH = MAX_BUNDLE_STATUS_LENGTH;

function normalizeBundleTierItem(value: unknown): SteamBundleTierItem | null {
  const record = isRecord(value) ? value : null;
  const title = toBoundedText(record === null ? value : read(record, "title"), MAX_BUNDLE_TITLE_LENGTH);
  if (title === null) return null;

  return {
    title,
    type: toBoundedText(record === null ? null : read(record, "type"), MAX_BUNDLE_TITLE_LENGTH),
    priceMinor: toPriceMinor(record === null ? null : read(record, "priceMinor")),
    priceCurrency: toCurrencyCode(record === null ? null : read(record, "priceCurrency"))
  };
}

function normalizeBundleTier(value: unknown): SteamBundleTier | null {
  if (!isRecord(value)) return null;

  const games: SteamBundleTierItem[] = [];
  const rawGames = read(value, "games");
  if (Array.isArray(rawGames)) {
    for (const entry of rawGames) {
      const item = normalizeBundleTierItem(entry);
      if (item === null) continue;
      games.push(item);
      if (games.length === MAX_BUNDLE_TIER_GAMES) break;
    }
  }

  return {
    priceMinor: toPriceMinor(read(value, "priceMinor")),
    currency: toCurrencyCode(read(value, "currency")),
    addon: read(value, "addon") === true,
    itemsComplete: read(value, "itemsComplete") !== false,
    games,
    status: toBoundedText(read(value, "status"), STEAM_BUNDLE_STATUS_LENGTH),
    reason: toBoundedText(read(value, "reason"), STEAM_BUNDLE_STATUS_LENGTH),
    individualTotalMinor: toPriceMinor(read(value, "individualTotalMinor")),
    bundlePriceMinor: toPriceMinor(read(value, "bundlePriceMinor")),
    savingsMinor: toSignedMinor(read(value, "savingsMinor")),
    savingsPercent: toSignedPercent(read(value, "savingsPercent")),
    fxRate: toPositiveDecimal(read(value, "fxRate")),
    fxRateDate: toIsoDate(read(value, "fxRateDate")),
    fxSource: toText(read(value, "fxSource")),
    pricingType: toPricingType(read(value, "pricingType")),
    mxnIndividualTotalMinor: toPriceMinor(read(value, "mxnIndividualTotalMinor")),
    mxnSavingsMinor: toSignedMinor(read(value, "mxnSavingsMinor"))
  };
}

function normalizeBundle(value: unknown): SteamGameBundle | null {
  if (!isRecord(value)) return null;

  // El título es la única identidad mínima: sin él no hay nada que mostrar ni enlazar.
  const title = toBoundedText(read(value, "title"), MAX_BUNDLE_TITLE_LENGTH);
  if (title === null) return null;

  const tiers: SteamBundleTier[] = [];
  const rawTiers = read(value, "tiers");
  if (Array.isArray(rawTiers)) {
    for (const entry of rawTiers) {
      const tier = normalizeBundleTier(entry);
      if (tier === null) continue;
      tiers.push(tier);
      if (tiers.length === MAX_BUNDLE_TIERS) break;
    }
  }

  return {
    bundleKey: toBoundedText(
      read(value, "bundleKey") ?? read(value, "providerBundleId") ?? read(value, "id"),
      MAX_BUNDLE_TITLE_LENGTH
    ),
    title,
    shopName: toBoundedText(read(value, "shopName"), MAX_BUNDLE_SHOP_NAME_LENGTH),
    pageUrl: toHttpsUrl(read(value, "pageUrl")),
    dealUrl: toHttpsUrl(read(value, "dealUrl")),
    details: toBoundedText(read(value, "details"), MAX_BUNDLE_DETAILS_LENGTH),
    publishedAt: toIsoDateTime(read(value, "publishedAt")),
    expiresAt: toIsoDateTime(read(value, "expiresAt")),
    observedAt: toIsoDateTime(read(value, "observedAt")),
    tiers
  };
}

export function normalizeSteamBundles(input: unknown): readonly SteamGameBundle[] {
  if (!Array.isArray(input)) return [];

  const bundles: SteamGameBundle[] = [];
  for (const entry of input) {
    const bundle = normalizeBundle(entry);
    if (bundle === null) continue;
    bundles.push(bundle);
    if (bundles.length === MAX_BUNDLES) break;
  }
  return bundles;
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
    offersStale: read(value, "offersStale") === true,
    ggDealsRefreshedAt: toIsoDateTime(read(value, "ggDealsRefreshedAt")),
    ggDealsStale: read(value, "ggDealsStale") === true,
    bundles: normalizeSteamBundles(read(value, "bundles")),
    bundlesRefreshedAt: toIsoDateTime(read(value, "bundlesRefreshedAt")),
    bundlesStale: read(value, "bundlesStale") === true
  };
}
