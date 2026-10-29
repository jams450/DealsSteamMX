// Reglas puras de la compra de un bundle (S4/S5): qué se selecciona, cuántos juegos exactos pide un tier
// de selección, qué ítem necesita vínculo manual y qué cuerpo exacto se manda. Sin red y sin reloj: `now`
// se inyecta, así que los límites de fecha se prueban con `node --test` sin navegador ni base de datos.
//
// Decisiones de producto que este módulo hace explícitas:
// - Tier fijo: la membresía elegible del servidor es la selección. No se puede desmarcar nada y el cuerpo
//   manda todos los elegibles; un ítem sin identidad bloquea hasta que se vincule, nunca se omite.
// - Tier de selección: exactamente `requiredSelectionCount` juegos de los elegibles. Ni el título ni el
//   orden de la lista eligen por el usuario: solo sus clics.
// - El cuerpo manda `canonicalGameId: null` para un ítem identificado por ITAD (el servidor resuelve) y un
//   id explícito solo para un ítem vinculado a mano. Una ficha elegida en la UI nunca sustituye una
//   identidad de ITAD.
// - La plataforma de la compra es una llave de StoreKeys (el contrato backend valida ese vocabulario). El
//   override por juego es opcional; ausente hereda la del bundle. Steam es el default de la UI porque la
//   mayoría de los bundles venden llaves de Steam: NO se deduce del shop, que solo dice quién vende.
import { STORE_KEYS, normalizeStore, storeLabel } from "../../../lib/contracts/stores.ts";
import {
  PURCHASE_PROVIDERS,
  PURCHASE_STORE_TYPES,
  normalizeBundlePurchaseRequest,
  type BundlePurchasePreview,
  type BundlePurchasePreviewItem,
  type BundlePurchasePreviewTier,
  type BundlePurchaseRequestBody,
  type BundlePurchaseResult,
  type PurchaseProvider,
  type PurchaseStoreType,
  type TierBlockedReason
} from "../../../lib/contracts/bundle-purchases.ts";

/** Plataforma por defecto de la compra. El usuario la cambia en el diálogo; nunca se deduce del shop. */
export const PURCHASE_PLATFORM_DEFAULT = "steam";

/** Selector de plataforma: el vocabulario cerrado que el backend valida (StoreKeys). */
export const PURCHASE_PLATFORM_OPTIONS: readonly { readonly value: string; readonly label: string }[] =
  STORE_KEYS.map((value) => ({ value, label: storeLabel(value) }));

// Mismos hechos manuales que el alta de la wishlist (S6 de L4), con la misma clasificación conservadora:
// no se deducen del proveedor y el default no afirma nada sobre la licencia de la tienda.
export const ACQUIRED_STORE_TYPE_OPTIONS: readonly {
  readonly value: PurchaseStoreType;
  readonly label: string;
}[] = [
  { value: "official", label: "Oficial / autorizada" },
  { value: "keyshop", label: "Keyshop / mercado gris" },
  { value: "unknown", label: "No sé" }
];

export const ACQUISITION_PROVIDER_OPTIONS: readonly {
  readonly value: PurchaseProvider;
  readonly label: string;
}[] = [
  { value: "itad", label: "IsThereAnyDeal" },
  { value: "ggdeals", label: "gg.deals" },
  { value: "direct", label: "Tienda directa" },
  { value: "other", label: "Otro" }
];

export const ACQUIRED_STORE_TYPE_DEFAULT: PurchaseStoreType = "unknown";

// Un motivo publicado se explica con una frase; nunca se pinta solo el código.
export const TIER_BLOCKED_REASON_LABELS: Record<TierBlockedReason, string> = {
  items_incomplete: "El proveedor no publicó el contenido completo del tier: no se puede registrar.",
  addon_tier: "Tier addon: no incluye juegos propios que importar.",
  no_eligible_games: "El tier no lista juegos elegibles que importar.",
  invalid_selection_count: "El tier no publica cuántos juegos hay que elegir: no se puede confirmar."
};

const GENERIC_BLOCKED = "Este tier no se puede registrar todavía.";

const ACQUISITION_DATE = /^\d{4}-\d{2}-\d{2}$/;

export type ManualLink = { readonly gameId: number; readonly title: string };

export type TierSelectionAttempt =
  | { readonly ok: true; readonly selected: readonly string[] }
  | { readonly ok: false; readonly message: string };

export type SelectionStatus = {
  readonly selectedCount: number;
  readonly requiredCount: number;
  readonly complete: boolean;
  readonly message: string;
};

export type PurchaseFeedback = {
  readonly tone: "success" | "info";
  readonly headline: string;
  readonly details: readonly string[];
};

export type PurchaseConfirmInput = {
  readonly preview: BundlePurchasePreview;
  readonly tier: BundlePurchasePreviewTier | null;
  /** Solo se usa en un tier de selección; un tier fijo calcula su membresía desde el servidor. */
  readonly selectedKeys: readonly string[];
  readonly itemPlatforms: Readonly<Record<string, string>>;
  readonly manualLinks: Readonly<Record<string, ManualLink>>;
  readonly platform: string;
  readonly acquiredAt: string;
  readonly acquiredStore: string;
  readonly acquiredStoreType: string;
  readonly acquisitionProvider: string;
  /** Reloj inyectado: la fecha de compra no puede ser futura respecto a este instante. */
  readonly now: Date;
};

export type PurchaseConfirmValidation =
  | { readonly ok: true; readonly body: BundlePurchaseRequestBody }
  | { readonly ok: false; readonly error: string };

function tierItems(tier: BundlePurchasePreviewTier): readonly BundlePurchasePreviewItem[] {
  return tier.items;
}

export function eligibleItems(tier: BundlePurchasePreviewTier): readonly BundlePurchasePreviewItem[] {
  return tierItems(tier).filter((item) => item.eligible);
}

/** Tier que abre el diálogo: el primero que de verdad se puede registrar; si no hay, el primero listado. */
export function defaultTierKey(preview: BundlePurchasePreview): string | null {
  const importable = preview.tiers.find((tier) => tier.importable);
  return (importable ?? preview.tiers[0])?.tierKey ?? null;
}

/** Selección inicial: membresía completa en un tier fijo, nada en uno de selección. */
export function initialSelection(tier: BundlePurchasePreviewTier | null): readonly string[] {
  if (tier === null || tier.selectionMode !== "fixed") return [];
  return eligibleItems(tier).map((item) => item.itemKey);
}

/** Ordena por el orden del tier (el del servidor), no por el orden de los clics. */
function orderedSelection(tier: BundlePurchasePreviewTier, keys: readonly string[]): readonly string[] {
  const wanted = new Set(keys);
  return tierItems(tier)
    .filter((item) => wanted.has(item.itemKey))
    .map((item) => item.itemKey);
}

/**
 * Marca o desmarca un juego. Un tier fijo no se edita; un tier de selección no pasa de su cupo exacto y
 * no acepta ítems que el servidor no marcó como elegibles.
 */
export function toggleSelection(
  tier: BundlePurchasePreviewTier | null,
  selected: readonly string[],
  itemKey: string
): TierSelectionAttempt {
  if (tier === null) return { ok: false, message: "Elige un tier del bundle para continuar." };
  if (tier.selectionMode === "fixed") {
    return {
      ok: false,
      message: "Este tier es fijo: se importan automáticamente sus juegos elegibles."
    };
  }

  const item = tierItems(tier).find((candidate) => candidate.itemKey === itemKey);
  if (item === undefined) return { ok: false, message: "Ese juego no está en el tier." };
  if (!item.eligible) {
    return { ok: false, message: `«${item.title}» no es un juego elegible de este tier.` };
  }

  if (selected.includes(itemKey)) {
    return { ok: true, selected: orderedSelection(tier, selected.filter((key) => key !== itemKey)) };
  }

  const required = tier.requiredSelectionCount;
  if (required !== null && selected.length >= required) {
    return {
      ok: false,
      message: `Este tier pide exactamente ${required} juegos y ya elegiste ${selected.length}. Desmarca uno para cambiar.`
    };
  }

  return { ok: true, selected: orderedSelection(tier, [...selected, itemKey]) };
}

/** Progreso visible: texto con el cupo exacto, nunca solo un color o un número suelto. */
export function selectionStatus(tier: BundlePurchasePreviewTier | null, selected: readonly string[]): SelectionStatus {
  if (tier === null) return { selectedCount: 0, requiredCount: 0, complete: false, message: "Elige un tier." };
  if (tier.selectionMode === "fixed") {
    const count = eligibleItems(tier).length;
    return {
      selectedCount: count,
      requiredCount: count,
      complete: count > 0,
      message:
        count === 1
          ? "Tier fijo: se importa automáticamente 1 juego elegible."
          : `Tier fijo: se importan automáticamente ${count} juegos elegibles.`
    };
  }
  const required = tier.requiredSelectionCount ?? 0;
  return {
    selectedCount: selected.length,
    requiredCount: required,
    complete: required > 0 && selected.length === required,
    message: `Seleccionados ${selected.length} de ${required}`
  };
}

/**
 * Juegos de la selección que todavía no tienen identidad y necesitan un vínculo del catálogo. Un ítem con
 * `itadGameId` nunca aparece aquí: su identidad ya la resolvió ITAD.
 */
export function unlinkedItems(
  tier: BundlePurchasePreviewTier | null,
  selectedKeys: readonly string[],
  manualLinks: Readonly<Record<string, ManualLink>>
): readonly BundlePurchasePreviewItem[] {
  if (tier === null) return [];
  const wanted = new Set(selectedKeys);
  return tierItems(tier).filter(
    (item) => wanted.has(item.itemKey) && item.requiresManualLink && manualLinks[item.itemKey] === undefined
  );
}

/** Etiqueta de elegibilidad de cada ítem del tier; el tipo solo se nombra cuando no es un juego elegible. */
export function eligibilityLabel(item: BundlePurchasePreviewItem): string {
  return item.eligible ? "Se importa" : `No elegible (${item.type ?? "sin tipo"})`;
}

function pad(value: number): string {
  return value < 10 ? `0${value}` : String(value);
}

/** Día del calendario local del navegador, sin pasar por UTC. */
export function localIsoDate(now: Date): string {
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/** Día UTC, que es el reloj con el que el backend decide si una fecha es futura. */
export function utcIsoDate(now: Date): string {
  return now.toISOString().slice(0, 10);
}

/**
 * Fecha por defecto del formulario: el día local, salvo que el día UTC vaya por detrás (husos al este de
 * UTC a primera hora). Se elige el menor para no ofrecer por defecto una fecha que el servidor rechace.
 */
export function acquisitionDateDefault(now: Date): string {
  const local = localIsoDate(now);
  const utc = utcIsoDate(now);
  return local <= utc ? local : utc;
}

function parseCalendarDate(value: string): string | null {
  if (!ACQUISITION_DATE.test(value)) return null;
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isNaN(date.getTime()) || date.toISOString().slice(0, 10) !== value ? null : value;
}

/**
 * Tienda de adquisición propuesta a partir del nombre del shop del bundle. Es una sugerencia editable, no
 * una deducción de plataforma: el shop dice quién vende (Fanatical, Humble, Eneba), no dónde se juega.
 */
export function defaultAcquiredStore(shopName: string | null): string | null {
  if (shopName === null) return null;
  const slug = shopName
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "")
    .slice(0, 32);
  return normalizeStore(slug);
}

/** Proveedor donde se vio la oferta: hecho declarado, con la fuente del bundle como propuesta. */
export function defaultProviderForSource(source: string): PurchaseProvider {
  return source === "ggdeals" ? "ggdeals" : source === "itad" ? "itad" : "other";
}

/**
 * Cuerpo exacto de la confirmación, o el primer motivo por el que no se puede confirmar. El último paso
 * vuelve a pasar por el normalizador del contrato (el mismo que usa el BFF): si algo diverge, se ve aquí
 * como un error de la UI, no como un 400 del servidor después de un clic.
 */
export function validatePurchaseConfirm(input: PurchaseConfirmInput): PurchaseConfirmValidation {
  const { tier } = input;
  if (tier === null) return { ok: false, error: "Elige un tier del bundle para continuar." };
  if (!tier.importable) {
    return {
      ok: false,
      error: tier.blockedReason === null ? GENERIC_BLOCKED : TIER_BLOCKED_REASON_LABELS[tier.blockedReason]
    };
  }

  // El backend normaliza con `StoreKeys`, que conoce las tiendas y los slugs de plataforma. Aquí se exige
  // una llave ya canónica: el selector de la UI ofrece las tiendas que la app sigue, y el servidor vuelve a
  // validar cualquier slug que llegue por otra vía.
  const platform = normalizeStore(input.platform);
  if (platform === null || platform !== input.platform) {
    return { ok: false, error: "Elige una plataforma válida para la compra (Steam, GOG, Epic…)." };
  }

  const itemsByKey = new Map(tierItems(tier).map((item) => [item.itemKey, item]));

  let selectedKeys: readonly string[];
  if (tier.selectionMode === "fixed") {
    // Membresía real del tier: el usuario no la edita y este módulo no la recorta. La selección de la UI
    // no participa, así que un clic viejo de otra versión de la vista previa no puede bloquear la compra.
    selectedKeys = eligibleItems(tier).map((item) => item.itemKey);
  } else {
    // Un ítem que ya no está (o dejó de ser elegible) se descarta: el cupo exacto vuelve a comprobarse
    // abajo, así que la compra no puede confirmarse con menos juegos de los que el tier pide.
    selectedKeys = orderedSelection(
      tier,
      input.selectedKeys.filter((key) => itemsByKey.get(key)?.eligible === true)
    );
    const required = tier.requiredSelectionCount ?? 0;
    if (required <= 0 || selectedKeys.length !== required) {
      return {
        ok: false,
        error: `Este tier pide exactamente ${required} juegos: llevas ${selectedKeys.length}.`
      };
    }
  }

  if (selectedKeys.length === 0) {
    return { ok: false, error: "Este tier no tiene juegos elegibles que importar." };
  }

  const pending = unlinkedItems(tier, selectedKeys, input.manualLinks);
  if (pending.length > 0) {
    return {
      ok: false,
      error: `Falta vincular «${pending[0]?.title ?? "un juego"}» con una ficha del catálogo antes de confirmar.`
    };
  }

  const acquiredAt = parseCalendarDate(input.acquiredAt);
  if (acquiredAt === null) {
    return { ok: false, error: "La fecha de compra no es válida (usa AAAA-MM-DD)." };
  }
  if (acquiredAt > utcIsoDate(input.now)) {
    return { ok: false, error: "La fecha de compra no puede ser futura." };
  }

  const acquiredStore = normalizeStore(input.acquiredStore);
  if (acquiredStore === null) {
    return {
      ok: false,
      error: "Escribe la tienda donde lo compraste (minúsculas y sin espacios; por ejemplo «fanatical»)."
    };
  }

  if (!PURCHASE_STORE_TYPES.some((candidate) => candidate === input.acquiredStoreType)) {
    return { ok: false, error: "Elige el tipo de tienda: oficial/autorizada, keyshop/mercado gris o «no sé»." };
  }
  if (!PURCHASE_PROVIDERS.some((candidate) => candidate === input.acquisitionProvider)) {
    return { ok: false, error: "Elige dónde viste la oferta: IsThereAnyDeal, gg.deals, tienda directa u otro." };
  }

  const items: { itemKey: string; platform: string | null; canonicalGameId: number | null }[] = [];
  for (const itemKey of selectedKeys) {
    const item = itemsByKey.get(itemKey);
    if (item === undefined) continue;

    const rawOverride = input.itemPlatforms[itemKey];
    let override: string | null = null;
    if (rawOverride !== undefined && rawOverride !== "") {
      override = normalizeStore(rawOverride);
      if (override === null || override !== rawOverride) {
        return { ok: false, error: `La plataforma de «${item.title}» no es válida.` };
      }
    }

    // Regla del contrato: identidad de ITAD ⇒ null (la resuelve el servidor); sin identidad ⇒ el id
    // vinculado a mano o el que el servidor ya conocía. Nunca se inventa uno.
    const canonicalGameId =
      item.itadGameId !== null ? null : (input.manualLinks[itemKey]?.gameId ?? item.canonicalGameId);
    if (canonicalGameId === null && item.requiresManualLink) {
      return {
        ok: false,
        error: `Falta vincular «${item.title}» con una ficha del catálogo antes de confirmar.`
      };
    }

    items.push({ itemKey, platform: override, canonicalGameId });
  }

  const body: BundlePurchaseRequestBody = {
    source: input.preview.source,
    bundleKey: input.preview.bundleKey,
    tierKey: tier.tierKey,
    platform,
    snapshotHash: input.preview.snapshotHash,
    acquiredAt,
    acquiredStore,
    acquiredStoreType: input.acquiredStoreType as PurchaseStoreType,
    acquisitionProvider: input.acquisitionProvider as PurchaseProvider,
    items
  };

  const normalized = normalizeBundlePurchaseRequest(body);
  if (normalized === null) {
    return {
      ok: false,
      error: "La compra no tiene la forma que espera el servidor; recarga la vista previa e inténtalo de nuevo."
    };
  }
  return { ok: true, body: normalized };
}

/**
 * Copy del resultado. `alreadyReported` es un reenvío idempotente del servidor, no un segundo alta: se
 * dice en voz alta que no se escribió nada nuevo. Nunca se promete ni se muestra un código de activación.
 */
export function purchaseFeedback(result: BundlePurchaseResult): PurchaseFeedback {
  const details: string[] = [];

  if (result.alreadyReported || !result.created) {
    details.push("No se escribió nada nuevo: esta compra ya se había registrado con su importación anterior.");
  } else {
    details.push(
      result.importedCount === 1
        ? "1 juego nuevo pasó a tu biblioteca."
        : `${result.importedCount} juegos nuevos pasaron a tu biblioteca.`
    );
  }

  if (result.duplicateCount > 0) {
    details.push(
      result.duplicateCount === 1
        ? "1 juego ya estaba en tu biblioteca en la misma plataforma: quedó registrado en Keys duplicadas, sin duplicar la fila."
        : `${result.duplicateCount} juegos ya estaban en tu biblioteca en la misma plataforma: quedaron registrados en Keys duplicadas, sin duplicar filas.`
    );
  }

  if (result.wishlistRemovedCount > 0) {
    details.push(
      result.wishlistRemovedCount === 1
        ? "1 juego se quitó de tu wishlist local, con protección para que la próxima sincronización de Steam no lo vuelva a añadir."
        : `${result.wishlistRemovedCount} juegos se quitaron de tu wishlist local, con protección para que la próxima sincronización de Steam no los vuelva a añadir.`
    );
  }

  if (details.length === 0) {
    details.push("El servidor no reportó juegos nuevos que importar.");
  }

  return {
    tone: result.alreadyReported || !result.created ? "info" : "success",
    headline: result.alreadyReported || !result.created ? "Esta compra ya estaba registrada." : "Compra registrada.",
    details
  };
}

// El backend publica dos resultados (`imported` y `duplicate`). Cualquier otro código se muestra tal cual,
// sin inventarle un significado en español.
const OUTCOME_LABELS: Readonly<Record<string, string>> = {
  imported: "Importado",
  duplicate: "Duplicado"
};

export function outcomeLabel(outcome: string | null): string {
  if (outcome === null) return "Registrado";
  return OUTCOME_LABELS[outcome] ?? outcome;
}
