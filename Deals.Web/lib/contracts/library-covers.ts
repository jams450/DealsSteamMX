// Contrato del relleno de portadas de la biblioteca. El cliente manda un appid de Steam, nunca una URL:
// la portada la resuelve y la guarda el servidor (Steam CDN), igual que el resto de datos de proveedor.
// La portada es decorativa: ninguna operación de este contrato reclama identidad ni toca precios.

/** Tope de la pasada, el mismo número que valida el backend. */
export const COVER_SYNC_MAX_LIMIT = 100;

/** Tamaño de pasada que pide la grilla cuando el usuario no elige otro. */
export const COVER_SYNC_DEFAULT_LIMIT = 25;

export type LibraryCoverSyncReport = {
  /** Juegos canónicos de la biblioteca sin portada (se cuentan juegos, no filas). */
  readonly missing: number;
  /** De los anteriores, los que no tienen appid de Steam conocido: ahí solo sirve la elección manual. */
  readonly missingWithoutSteamId: number;
  readonly updated: number;
  readonly failed: number;
  /** Con appid conocido que quedaron para la siguiente pasada, porque la pasada es acotada. */
  readonly remaining: number;
};

type UnknownRecord = Record<string, unknown>;

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

function toCount(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : null;
}

/**
 * Cuerpo válido de una pasada: `{}` (el servidor aplica su default) o un `limit` entero 1..100. `null`
 * significa cuerpo inválido y la ruta responde 400: un límite fuera de rango nunca se recorta en silencio.
 */
export function parseCoverSyncRequest(input: unknown): { readonly limit?: number } | null {
  if (!isRecord(input)) return null;
  if (input.limit === undefined) return {};

  const limit = input.limit;
  return typeof limit === "number" &&
    Number.isSafeInteger(limit) &&
    limit >= 1 &&
    limit <= COVER_SYNC_MAX_LIMIT
    ? { limit }
    : null;
}

/** Reporte de la pasada, o `null` si algún contador falta o no es un entero no negativo. */
export function normalizeCoverSyncReport(input: unknown): LibraryCoverSyncReport | null {
  if (!isRecord(input)) return null;

  const missing = toCount(input.missing);
  const missingWithoutSteamId = toCount(input.missingWithoutSteamId);
  const updated = toCount(input.updated);
  const failed = toCount(input.failed);
  const remaining = toCount(input.remaining);

  if (
    missing === null ||
    missingWithoutSteamId === null ||
    updated === null ||
    failed === null ||
    remaining === null
  ) {
    return null;
  }

  return { missing, missingWithoutSteamId, updated, failed, remaining };
}

/** Cuerpo válido de una elección manual: un appid entero positivo, nada más. */
export function parseCoverPick(input: unknown): { readonly steamAppId: number } | null {
  if (!isRecord(input)) return null;
  const steamAppId = input.steamAppId;
  return typeof steamAppId === "number" && Number.isSafeInteger(steamAppId) && steamAppId > 0
    ? { steamAppId }
    : null;
}

/** URL de portada devuelta por el servidor, o `null` si no vino ninguna. */
export function normalizeCoverUrl(input: unknown): string | null {
  if (!isRecord(input)) return null;
  const imageUrl = input.imageUrl;
  return typeof imageUrl === "string" && imageUrl.trim() !== "" ? imageUrl : null;
}
