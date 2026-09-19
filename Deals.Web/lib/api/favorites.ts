import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import type { FavoriteTarget } from "@/lib/contracts/favorites";

/**
 * Marca o desmarca un favorito. El BFF responde 204 sin cuerpo, así que no hay nada que normalizar: el
 * llamador ya cambió el estado en pantalla y este es el espejo en el servidor.
 *
 * `csrfFetch` agrega el token CSRF; la identidad viaja en el cuerpo (`gameId` desde la biblioteca,
 * `steamAppId` desde el detalle).
 */
export async function setFavorite(target: FavoriteTarget, favorite: boolean): Promise<void> {
  const response = await csrfFetch("/api/bff/favorites", {
    method: favorite ? "POST" : "DELETE",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(target),
    cache: "no-store"
  });

  if (!response.ok) {
    throw await parseApiError(response, favorite ? "No se pudo marcar el favorito" : "No se pudo desmarcar el favorito");
  }
}
