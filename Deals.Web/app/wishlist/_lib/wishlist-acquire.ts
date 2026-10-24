// Alta de un juego de la wishlist en la biblioteca (L4). Este módulo es la frontera del formulario: arma el
// cuerpo exacto del contrato, valida antes de llamar al BFF y traduce el outcome a copy. No habla con la
// red, así que se puede probar con `node --test` sin navegador ni base de datos.
//
// El aviso de Steam es una regla de producto, no copy decorativa: el servidor nunca escribe en Steam. Si el
// texto llegara a prometer lo contrario, este test lo delata (ver `wishlist-acquire.test.ts`).
import { PLATFORM_CATALOG_KEYS, STORE_KEYS, normalizeStore, storeLabel } from "../../../lib/contracts/stores.ts";
import {
  ACQUISITION_PROVIDERS,
  ACQUIRED_STORE_TYPES,
  ACQUISITION_TYPES,
  type AcquisitionProvider,
  type AcquisitionType,
  type AcquiredStoreType,
  type WishlistAcquireBody,
  type WishlistAcquireResult
} from "./wishlist-contract.ts";

// Marca local del <select> que abre el campo libre. Nunca viaja al servidor.
export const ACQUIRE_PLATFORM_OTHER = "other";

// Tiendas conocidas primero (son la mayoría de los casos) y luego el catálogo de consolas. Es cosmético: el
// valor final lo valida `normalizeStore`, el mismo punto único del backend, así que una plataforma ausente
// de esta lista se escribe en «Otra plataforma…» sin tocar código.
const PLATFORM_VALUES: readonly string[] = [...new Set<string>([...STORE_KEYS, ...PLATFORM_CATALOG_KEYS])];

export const ACQUIRE_PLATFORM_OPTIONS: readonly { readonly value: string; readonly label: string }[] =
  PLATFORM_VALUES.map((value) => ({ value, label: storeLabel(value) }));

// La tienda donde se adquirió es otra pregunta: una consola no vende, vende una tienda. El slug libre sigue
// disponible en «Otra tienda…» porque el contrato acepta slugs personalizados.
export const ACQUIRE_STORE_OPTIONS: readonly { readonly value: string; readonly label: string }[] =
  STORE_KEYS.map((value) => ({ value, label: storeLabel(value) }));

// Los cinco tipos de L4, con su consecuencia visible: `subscription` entra como suscripción y no cuenta
// como compra, que es lo que el filtro «Compra» de la wishlist lee.
export const ACQUISITION_TYPE_OPTIONS: readonly {
  readonly value: AcquisitionType;
  readonly label: string;
  readonly hint: string;
}[] = [
  { value: "purchase", label: "Compra", hint: "Lo pagaste tú." },
  { value: "gift", label: "Regalo", hint: "Te lo regalaron." },
  { value: "free", label: "Gratis", hint: "Lo obtuviste sin pagar (free to keep)." },
  { value: "bundle", label: "Paquete (bundle)", hint: "Vino dentro de un paquete." },
  { value: "subscription", label: "Suscripción", hint: "Entra como suscripción, no como compra." }
];

// S4 «solo aviso»: el servidor no tiene cliente de escritura de Steam y no lo tendrá. El texto manda al
// usuario a hacerlo a mano; el segundo párrafo explica la única parte automática, que es local.
export const STEAM_REMOVAL_NOTICE =
  "Solo aviso: no tocamos tu cuenta de Steam. Si quieres sacarlo también de tu wishlist de Steam, hazlo manualmente en Steam.";
export const STEAM_REMOVAL_SYNC_NOTE =
  "Si lo quitas solo aquí, la próxima sincronización de Steam no volverá a añadirlo a tu wishlist local.";

// S6: dos selectores nuevos, distintos entre sí y de la plataforma. «Tipo de tienda» dice si la tienda vende con
// licencia del editor (oficial/autorizada) o es mercado gris (keyshop); «Oferta encontrada en» dice dónde se vio
// la oferta. Son hechos que el usuario declara, no deducciones: ITAD y gg.deals listan ambas clases de tienda, así
// que el proveedor jamás decide la clasificación. Los defaults son explícitos y conservadores: no sé + tienda
// directa, que no afirma nada sobre la licencia de la tienda.
export const ACQUIRE_STORE_TYPE_LABEL = "Tipo de tienda";
export const ACQUIRE_PROVIDER_LABEL = "Oferta encontrada en";

export const ACQUIRE_STORE_TYPE_DEFAULT: AcquiredStoreType = "unknown";
export const ACQUIRE_PROVIDER_DEFAULT: AcquisitionProvider = "direct";

export const ACQUIRED_STORE_TYPE_OPTIONS: readonly {
  readonly value: AcquiredStoreType;
  readonly label: string;
}[] = [
  { value: "official", label: "Oficial / autorizada" },
  { value: "keyshop", label: "Keyshop / mercado gris" },
  { value: "unknown", label: "No sé" }
];

export const ACQUISITION_PROVIDER_OPTIONS: readonly {
  readonly value: AcquisitionProvider;
  readonly label: string;
}[] = [
  { value: "itad", label: "IsThereAnyDeal" },
  { value: "ggdeals", label: "gg.deals" },
  { value: "direct", label: "Tienda directa" },
  { value: "other", label: "Otro" }
];

// Ejemplo con nombres que el usuario reconoce (Eneba es keyshop, gg.deals es donde se ve la oferta), dejando
// claro que la plataforma se pregunta aparte y que clasificar la tienda no es clasificar el sitio.
export const ACQUIRE_STORE_TYPE_HINT =
  "Eneba / Keyshop / gg.deals: clasifica la tienda y di dónde viste la oferta. La plataforma va aparte.";

// El descarte local viene marcado por omisión: quien acaba de comprar quiere dejar de verlo en la wishlist, y
// el aviso de al lado dice en voz alta que Steam no se toca. Desmarcarlo es explícito y no suprime nada.
export const ACQUIRE_REMOVE_DEFAULT = true;

const ACQUISITION_DATE = /^\d{4}-\d{2}-\d{2}$/;

// Fecha de adquisición opcional. El vacío es `null` (nunca la fecha de hoy: sería una fecha inventada); una
// cadena que no sea un día real del calendario se rechaza, porque el regex por sí solo acepta 2026-02-30.
export type AcquisitionDateParse =
  | { readonly ok: true; readonly value: string | null }
  | { readonly ok: false };

export function parseAcquisitionDate(value: string): AcquisitionDateParse {
  const text = typeof value === "string" ? value.trim() : "";
  if (text.length === 0) return { ok: true, value: null };
  if (!ACQUISITION_DATE.test(text)) return { ok: false };

  const date = new Date(`${text}T00:00:00Z`);
  if (Number.isNaN(date.getTime()) || date.toISOString().slice(0, 10) !== text) return { ok: false };
  return { ok: true, value: text };
}

// El BFF recibe JSON crudo, no el formulario: la forma se exige completa (los cinco campos del contrato) y
// ningún valor se completa por defecto. Un `removeFromWishlist` ausente no se lee como `false`, porque eso
// afirmaría en la respuesta algo que el cliente no pidió; un `acquiredAt` vacío tampoco se inventa como hoy.
//
// S6/L14: `acquiredStoreType` y `acquisitionProvider` son opcionales por diseño. Ausente o `null` valen (un
// cliente viejo no los manda) y se normalizan a `null`, **nunca** al default de la UI: rellenarlo afirmaría una
// clasificación o un proveedor que el usuario no eligió. Un valor fuera del vocabulario rechaza el cuerpo entero,
// porque descartarlo en silencio perdería el hecho que el usuario sí declaró.
type VocabularyRead<T extends string> =
  | { readonly ok: true; readonly value: T | null }
  | { readonly ok: false };

function readOptionalVocabulary<T extends string>(value: unknown, vocabulary: readonly T[]): VocabularyRead<T> {
  if (value === undefined || value === null) return { ok: true, value: null };
  const match = vocabulary.find((candidate) => candidate === value);
  return match === undefined ? { ok: false } : { ok: true, value: match };
}

export function normalizeAcquireRequestBody(input: unknown): WishlistAcquireBody | null {
  if (typeof input !== "object" || input === null || Array.isArray(input)) return null;
  const record = input as Record<string, unknown>;

  const platform = normalizeStore(record.platform);
  if (platform === null) return null;

  const acquiredStore = normalizeStore(record.acquiredStore);
  if (acquiredStore === null) return null;

  const acquisitionType = ACQUISITION_TYPES.find((candidate) => candidate === record.acquisitionType);
  if (acquisitionType === undefined) return null;

  const acquiredStoreType = readOptionalVocabulary(record.acquiredStoreType, ACQUIRED_STORE_TYPES);
  if (!acquiredStoreType.ok) return null;

  const acquisitionProvider = readOptionalVocabulary(record.acquisitionProvider, ACQUISITION_PROVIDERS);
  if (!acquisitionProvider.ok) return null;

  if (typeof record.removeFromWishlist !== "boolean") return null;

  let acquiredAt: string | null;
  if (record.acquiredAt === null) {
    acquiredAt = null;
  } else {
    if (typeof record.acquiredAt !== "string") return null;
    const date = parseAcquisitionDate(record.acquiredAt);
    // `parseAcquisitionDate` devuelve `null` para el vacío, que aquí no es una fecha ausente declarada:
    // el contrato pide `null` explícito, así que la cadena vacía se rechaza.
    if (!date.ok || date.value === null) return null;
    acquiredAt = date.value;
  }

  return {
    platform,
    acquiredStore,
    acquisitionType,
    acquiredAt,
    removeFromWishlist: record.removeFromWishlist,
    acquiredStoreType: acquiredStoreType.value,
    acquisitionProvider: acquisitionProvider.value
  };
}

export type AcquisitionFormValues = {
  readonly platform: string;
  readonly customPlatform?: string;
  readonly acquiredStore: string;
  readonly customAcquiredStore?: string;
  readonly acquisitionType: string;
  readonly acquiredAt: string;
  readonly removeFromWishlist: boolean;
  // S6: obligatorios en el formulario y prellenados con los defaults conservadores del diálogo. El valor que
  // llega aquí ya es una elección del usuario, así que el cuerpo lo manda tal cual.
  readonly acquiredStoreType: string;
  readonly acquisitionProvider: string;
};

export type AcquisitionValidation =
  | { readonly ok: true; readonly body: WishlistAcquireBody }
  | { readonly ok: false; readonly error: string };

// Un slug válido pero ausente del catálogo visible se acepta: el contrato permite slugs de tienda
// personalizados y la lista de arriba es solo pintura. Lo que no se acepta es texto que no es slug.
function resolveStore(value: string, customValue: string | undefined): string | null {
  return value === ACQUIRE_PLATFORM_OTHER ? normalizeStore(customValue ?? "") : normalizeStore(value);
}

// Fecha de referencia en UTC, igual que el backend (`DateOnly.FromDateTime(DateTime.UtcNow)`): una fecha
// futura se rechaza allí con 400 y aquí antes de gastar la llamada. Se inyecta en las pruebas.
function todayUtcDate(): string {
  return new Date().toISOString().slice(0, 10);
}

export function validateAcquisitionForm(values: AcquisitionFormValues, today: string = todayUtcDate()): AcquisitionValidation {
  const platform = resolveStore(values.platform, values.customPlatform);
  if (platform === null) {
    return {
      ok: false,
      error:
        values.platform === ACQUIRE_PLATFORM_OTHER
          ? "Escribe tu plataforma en minúsculas, por ejemplo «coleco»."
          : "Elige una plataforma."
    };
  }

  const acquiredStore = resolveStore(values.acquiredStore, values.customAcquiredStore);
  if (acquiredStore === null) {
    return {
      ok: false,
      error:
        values.acquiredStore === ACQUIRE_PLATFORM_OTHER
          ? "Escribe la tienda en minúsculas, por ejemplo «tienda_local»."
          : "Elige la tienda donde lo adquiriste."
    };
  }

  const acquisitionType = ACQUISITION_TYPES.find((candidate) => candidate === values.acquisitionType);
  if (acquisitionType === undefined) {
    return { ok: false, error: "Elige un tipo de adquisición válido." };
  }

  // S6: las dos respuestas se validan por separado. Que una falte o sea inválida nunca se resuelve rellenando
  // la otra: son hechos independientes (un keyshop visto en ITAD y una tienda oficial vista en gg.deals son
  // ambos válidos).
  const acquiredStoreType = ACQUIRED_STORE_TYPES.find((candidate) => candidate === values.acquiredStoreType);
  if (acquiredStoreType === undefined) {
    return { ok: false, error: "Elige el tipo de tienda: oficial/autorizada, keyshop/mercado gris o «no sé»." };
  }

  const acquisitionProvider = ACQUISITION_PROVIDERS.find((candidate) => candidate === values.acquisitionProvider);
  if (acquisitionProvider === undefined) {
    return { ok: false, error: "Elige dónde encontraste la oferta: IsThereAnyDeal, gg.deals, tienda directa u otro." };
  }

  if (typeof values.removeFromWishlist !== "boolean") {
    return { ok: false, error: "Indica si quieres quitarlo de tu wishlist local." };
  }

  const date = parseAcquisitionDate(values.acquiredAt);
  if (!date.ok) {
    return { ok: false, error: "La fecha de adquisición no es válida (usa AAAA-MM-DD)." };
  }
  // Misma regla y mismo texto que la API: una compra no puede haber ocurrido mañana.
  if (date.value !== null && date.value > today) {
    return { ok: false, error: "La fecha de adquisición no puede ser futura." };
  }

  return {
    ok: true,
    body: {
      platform,
      acquiredStore,
      acquisitionType,
      acquiredAt: date.value,
      removeFromWishlist: values.removeFromWishlist,
      acquiredStoreType,
      acquisitionProvider
    }
  };
}

export type AcquisitionFeedback = {
  readonly tone: "success" | "info";
  readonly message: string;
};

// El outcome lo decide el servidor. `already_owned` no es un error (el alta es idempotente y conserva la
// metadata previa), así que se cuenta como información, no como fallo.
export function acquisitionFeedback(result: WishlistAcquireResult): AcquisitionFeedback {
  const suffix = result.dismissed
    ? " Lo quitamos de tu wishlist local: la próxima sincronización de Steam no volverá a añadirlo."
    : " Sigue en tu wishlist local.";

  if (result.outcome === "already_owned") {
    return { tone: "info", message: `Ya estaba en tu biblioteca: se conservó su registro y no se duplicó.${suffix}` };
  }

  return { tone: "success", message: `Añadido a tu biblioteca.${suffix}` };
}
