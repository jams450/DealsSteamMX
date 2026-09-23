import { parseApiError } from "@/lib/bff/client-session";
import { csrfFetch } from "@/lib/security/csrf-client";
import {
  normalizeGameTitleResult,
  type GameTitleEditRequest,
  type GameTitleResult
} from "@/lib/contracts/game-title";

const INVALID_RESULT = "El servidor devolvió un resultado de edición de título inválido";

/**
 * Edita el título canónico de un juego. `manual` guarda el texto escrito; `igdb` guarda el título del id
 * elegido en la búsqueda (el servidor lo resuelve, el cliente nunca manda un título en ese modo).
 *
 * El 409 es un rechazo normal — nada se escribió — y se normaliza igual que el 200 para que el diálogo
 * muestre el motivo. Cualquier otro status (400/404/503/5xx) es un error y se lanza tipado.
 */
export async function editGameTitle(gameId: number, input: GameTitleEditRequest): Promise<GameTitleResult> {
  const response = await csrfFetch(`/api/bff/games/${gameId}/title`, {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(input),
    cache: "no-store"
  });

  if (response.status !== 200 && response.status !== 409) {
    throw await parseApiError(response, "No se pudo guardar el título");
  }

  const result = normalizeGameTitleResult(await response.json().catch(() => null));
  if (result === null) throw new Error(INVALID_RESULT);
  return result;
}
