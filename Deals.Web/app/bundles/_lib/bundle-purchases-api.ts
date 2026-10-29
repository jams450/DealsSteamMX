import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  normalizeBundlePurchaseDuplicates,
  normalizeBundlePurchaseList,
  normalizeBundlePurchasePreview,
  normalizeBundlePurchaseResult,
  normalizeCatalogCandidates,
  type BundlePurchaseDuplicateList,
  type BundlePurchaseList,
  type BundlePurchasePreview,
  type BundlePurchaseRequestBody,
  type BundlePurchaseResult,
  type CatalogCandidate
} from "@/lib/contracts/bundle-purchases";

// Llamadas del cliente a los BFF de compra de bundles. Toda mutación pasa por `csrfFetch` (doble envío) y
// toda respuesta se normaliza con el contrato compartido: la UI nunca lee un JSON crudo del servidor.
// El usuario no viaja en ninguna URL ni cuerpo: lo pone la sesión del lado del servidor.

const INVALID_PREVIEW = "El servidor devolvió una vista previa de compra inválida.";
const INVALID_RESULT = "El servidor devolvió un resultado de compra inválido.";
const INVALID_HISTORY = "El servidor devolvió un historial de compras inválido.";
const INVALID_DUPLICATES = "El servidor devolvió una lista de duplicados inválida.";

export async function getBundlePurchasePreview(
  source: string,
  bundleKey: string,
  platform?: string | null,
  signal?: AbortSignal
): Promise<BundlePurchasePreview> {
  const params = new URLSearchParams({ source, bundleKey });
  if (platform) params.set("platform", platform);
  const response = await csrfFetch(`/api/bff/bundles/purchases/preview?${params.toString()}`, {
    cache: "no-store",
    signal
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo leer la vista previa del bundle");
  const preview = normalizeBundlePurchasePreview(await response.json().catch(() => null));
  if (preview === null) throw new Error(INVALID_PREVIEW);
  return preview;
}

export async function createBundlePurchase(body: BundlePurchaseRequestBody): Promise<BundlePurchaseResult> {
  const response = await csrfFetch("/api/bff/bundles/purchases", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo registrar la compra del bundle");
  const result = normalizeBundlePurchaseResult(await response.json().catch(() => null));
  if (result === null) throw new Error(INVALID_RESULT);
  return result;
}

export async function getBundlePurchases(signal?: AbortSignal): Promise<BundlePurchaseList> {
  const response = await csrfFetch("/api/bff/bundles/purchases", { cache: "no-store", signal });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar tu historial de compras");
  const list = normalizeBundlePurchaseList(await response.json().catch(() => null));
  if (list === null) throw new Error(INVALID_HISTORY);
  return list;
}

export async function getBundlePurchaseDuplicates(signal?: AbortSignal): Promise<BundlePurchaseDuplicateList> {
  const response = await csrfFetch("/api/bff/bundles/purchases/duplicates", { cache: "no-store", signal });
  if (!response.ok) throw await parseApiError(response, "No se pudieron cargar los juegos duplicados");
  const duplicates = normalizeBundlePurchaseDuplicates(await response.json().catch(() => null));
  if (duplicates === null) throw new Error(INVALID_DUPLICATES);
  return duplicates;
}

export async function searchCatalogCandidates(
  query: string,
  signal?: AbortSignal
): Promise<readonly CatalogCandidate[]> {
  const params = new URLSearchParams({ q: query, limit: "10" });
  const response = await csrfFetch(`/api/bff/bundles/catalog-candidates?${params.toString()}`, {
    cache: "no-store",
    signal
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo buscar en el catálogo");
  return normalizeCatalogCandidates(await response.json().catch(() => null)).items;
}
