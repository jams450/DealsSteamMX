// Contrato de la edición del título canónico (`PUT /api/games/{gameId}/title`). El cuerpo es
// discriminado: `manual` con el título escrito, o `igdb` con el id cuya lectura hace el servidor. El
// cliente nunca manda los dos campos a la vez ni un título cuando el modo es `igdb`: esa ambigüedad es
// 400 en el backend y el normalizador la rechaza antes de gastar el viaje.
//
// Escribir aquí cambia `games.title` / `games.normalized_title`, o sea el nombre COMPARTIDO por todas las
// copias vinculadas al juego canónico, no el `user_library.title` que importó Playnite. Un 409 es un
// rechazo normal — nada se escribe — que trae su motivo; un 200 es el estado que el catálogo guardó.
//
// Rutas relativas a propósito: este módulo se cubre con `node --test`, que no resuelve el alias `@/`.

type UnknownRecord = Record<string, unknown>;

/** `games.title` es VARCHAR(512): el mismo tope que valida el backend. */
export const GAME_TITLE_MAX_LENGTH = 512;

/** Motivo del rechazo: es texto pensado para mostrarse tal cual, sin payloads ni ids de proveedor. */
const MAX_REASON_LENGTH = 2_000;

export type GameTitleSource = "manual" | "igdb";

export type GameTitleEditRequest =
  | { readonly mode: "manual"; readonly title: string }
  | { readonly mode: "igdb"; readonly igdbId: number };

/** 200: lo que el catálogo guardó. `igdbId` solo llega en modo `igdb`. */
export type GameTitleApplied = {
  readonly kind: "applied";
  readonly gameId: number;
  readonly title: string;
  readonly normalizedTitle: string;
  readonly source: GameTitleSource;
  readonly igdbId: number | null;
};

/** 409: el catálogo rechazó el cambio y no escribió nada. */
export type GameTitleConflict = {
  readonly kind: "conflict";
  readonly gameId: number | null;
  readonly reason: string;
};

export type GameTitleResult = GameTitleApplied | GameTitleConflict;

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

// El backend responde camelCase; se tolera PascalCase porque los DTOs de .NET pueden reconfigurarse.
function read(value: UnknownRecord, key: string): unknown {
  return value[key] ?? value[key.charAt(0).toUpperCase() + key.slice(1)];
}

function toText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function toBoundedText(value: unknown, maxLength: number): string | null {
  const text = toText(value);
  return text === null ? null : text.slice(0, maxLength);
}

function toPositiveInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : null;
}

// Un campo "no enviado" es `undefined` o `null`: el backend trata igual un `igdbId` nulo en modo manual.
function isAbsent(value: unknown): boolean {
  return value === undefined || value === null;
}

/**
 * Cuerpo válido de una edición, o `null`. Es estricto en las dos direcciones: `manual` exige un título
 * de 1..512 caracteres y prohíbe `igdbId`; `igdb` exige un id entero positivo y prohíbe `title`. Mandar
 * ambos (o ninguno) devuelve `null`: la forma es discriminada y no se adivina qué quiso el llamador.
 */
export function parseGameTitleRequest(input: unknown): GameTitleEditRequest | null {
  if (!isRecord(input)) return null;

  const mode = toText(read(input, "mode"))?.toLowerCase();
  const rawTitle = read(input, "title");
  const rawIgdbId = read(input, "igdbId");

  if (mode === "manual") {
    if (!isAbsent(rawIgdbId)) return null;

    const title = typeof rawTitle === "string" ? rawTitle.trim() : "";
    if (title.length === 0 || title.length > GAME_TITLE_MAX_LENGTH) return null;
    return { mode: "manual", title };
  }

  if (mode === "igdb") {
    if (!isAbsent(rawTitle)) return null;

    const igdbId = toPositiveInteger(rawIgdbId);
    return igdbId === null ? null : { mode: "igdb", igdbId };
  }

  return null;
}

/**
 * 200 del servidor. Rechaza el cuerpo de un 409 (`applied: false`) y cualquier respuesta a la que le
 * falte un campo obligatorio: una forma rota no puede pintarse como "título guardado".
 */
export function normalizeGameTitleApplied(input: unknown): GameTitleApplied | null {
  if (!isRecord(input)) return null;
  if (read(input, "applied") === false) return null;

  const gameId = toPositiveInteger(read(input, "gameId"));
  const title = toBoundedText(read(input, "title"), GAME_TITLE_MAX_LENGTH);
  const normalizedTitle = toBoundedText(read(input, "normalizedTitle"), GAME_TITLE_MAX_LENGTH);
  const source = toText(read(input, "source"))?.toLowerCase();

  if (gameId === null || title === null || normalizedTitle === null) return null;
  if (source !== "manual" && source !== "igdb") return null;

  if (source === "igdb") {
    // En modo `igdb` el id es la identidad recién reclamada: sin él la respuesta no prueba nada.
    const igdbId = toPositiveInteger(read(input, "igdbId"));
    if (igdbId === null) return null;
    return { kind: "applied", gameId, title, normalizedTitle, source, igdbId };
  }

  // Un `igdbId` en modo manual contradice la fuente declarada: se rechaza en vez de ignorarlo.
  if (!isAbsent(read(input, "igdbId"))) return null;
  return { kind: "applied", gameId, title, normalizedTitle, source, igdbId: null };
}

/**
 * 409 del servidor: `applied: false` y un motivo mostrable. El motivo es obligatorio (es lo único que la
 * UI puede enseñar); el `gameId` se conserva cuando llega y queda en `null` cuando no.
 */
export function normalizeGameTitleConflict(input: unknown): GameTitleConflict | null {
  if (!isRecord(input)) return null;
  if (read(input, "applied") !== false) return null;

  const reason = toBoundedText(read(input, "reason"), MAX_REASON_LENGTH);
  if (reason === null) return null;

  return { kind: "conflict", gameId: toPositiveInteger(read(input, "gameId")), reason };
}

/**
 * Resultado de una edición: `applied` decide la rama, como en la fusión. El 200 no trae `applied`, así
 * que cualquier forma que no sea un 409 explícito se intenta como acierto; lo que no encaje es `null`.
 */
export function normalizeGameTitleResult(input: unknown): GameTitleResult | null {
  if (isRecord(input) && read(input, "applied") === false) {
    return normalizeGameTitleConflict(input);
  }

  return normalizeGameTitleApplied(input);
}
