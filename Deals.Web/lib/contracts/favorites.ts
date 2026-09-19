// Contrato del interruptor de favorito. Una sola identidad viaja: `gameId` (la fila de biblioteca, que ya
// tiene el id canónico) o `steamAppId` (el detalle de Steam, que no lo tiene a mano). Nunca los dos ni
// ninguno: el backend lo rechaza y aquí se rechaza antes de salir.
export type FavoriteTarget =
  | { readonly gameId: number; readonly steamAppId?: undefined }
  | { readonly gameId?: undefined; readonly steamAppId: number };

type UnknownRecord = Record<string, unknown>;

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

function toPositiveInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : null;
}

/** Identidad válida del favorito, o `null` si no llega exactamente una. */
export function parseFavoriteTarget(input: unknown): FavoriteTarget | null {
  if (!isRecord(input)) return null;

  // La presencia de la clave decide, no su validez: con las dos claves (aunque una sea basura) no se
  // elige ninguna, porque adivinar cuál quiso el cliente puede marcar el juego equivocado.
  const hasGameId = input.gameId !== undefined;
  const hasSteamAppId = input.steamAppId !== undefined;
  if (hasGameId === hasSteamAppId) return null;

  if (hasGameId) {
    const gameId = toPositiveInteger(input.gameId);
    return gameId === null ? null : { gameId };
  }

  const steamAppId = toPositiveInteger(input.steamAppId);
  return steamAppId === null ? null : { steamAppId };
}
