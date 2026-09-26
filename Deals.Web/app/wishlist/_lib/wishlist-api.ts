import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  normalizeWishlistPackagePreview,
  normalizeWishlistPreferencesResponse,
  normalizeWishlistResponse,
  normalizeWishlistSyncResponse,
  type WishlistPackagePreview,
  type WishlistResponse,
  type WishlistSyncResponse
} from "./wishlist-contract";

const INVALID_RESPONSE = "El servidor devolvió una wishlist inválida";

export async function getWishlist(): Promise<WishlistResponse> {
  const response = await fetch("/api/bff/wishlist", { cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo cargar la wishlist");

  const wishlist = normalizeWishlistResponse(await response.json());
  if (wishlist === null) throw new Error(INVALID_RESPONSE);
  return wishlist;
}

export async function syncWishlist(): Promise<WishlistSyncResponse> {
  const response = await csrfFetch("/api/bff/wishlist/sync", { method: "POST", cache: "no-store" });
  if (!response.ok) throw await parseApiError(response, "No se pudo sincronizar la wishlist");

  const report = normalizeWishlistSyncResponse(await response.json());
  if (report === null) throw new Error("El servidor devolvió un reporte de sincronización inválido");
  return report;
}

// El preview del paquete es de solo lectura pero se pide por POST: la selección viaja en el cuerpo, no en
// la URL, y el servidor relee sus propios precios. El cliente nunca manda importes.
export async function previewWishlistPackage(appIds: readonly number[]): Promise<WishlistPackagePreview> {
  const response = await csrfFetch("/api/bff/wishlist/package-preview", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ appIds }),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo calcular el paquete");

  const preview = normalizeWishlistPackagePreview(await response.json());
  if (preview === null) throw new Error("El servidor devolvió un cálculo de paquete inválido");
  return preview;
}

// Devuelve el umbral confirmado por el servidor, no el que se envió: el backend es la fuente de verdad.
export async function updateWishlistPreferences(minViableDiscountPercent: number): Promise<number> {
  const response = await csrfFetch("/api/bff/wishlist/preferences", {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ minViableDiscountPercent }),
    cache: "no-store"
  });
  if (!response.ok) throw await parseApiError(response, "No se pudo guardar el descuento mínimo viable");

  const preferences = normalizeWishlistPreferencesResponse(await response.json());
  if (preferences === null) throw new Error("El servidor devolvió preferencias de wishlist inválidas");
  return preferences.minViableDiscountPercent;
}
