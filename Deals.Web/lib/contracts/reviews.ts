import { toStoreKey, type StoreKey } from "./stores.ts";

type UnknownRecord = Record<string, unknown>;

// Reseña de un juego en una plataforma. `platform` reutiliza el vocabulario de `stores.ts`, así que la
// reseña y la fila de biblioteca hablan la misma llave sin traducción. Los meses viajan como `YYYY-MM`
// (mapean directo a `<input type="month">`) y `scoreLabel` la calcula el backend: el cliente nunca
// replica los umbrales de los rangos.
export type Review = {
  readonly reviewId: number;
  readonly gameId: number;
  readonly platform: StoreKey;
  readonly startedMonth: string | null;
  readonly finishedMonth: string | null;
  readonly score: number | null;
  readonly scoreLabel: string | null;
  readonly isGoty: boolean;
  readonly body: string | null;
  readonly created: string | null;
  readonly updated: string | null;
};

// Alta: incluye la identidad `(gameId, platform)`.
export type ReviewCreateRequest = {
  readonly gameId: number;
  readonly platform: StoreKey;
  readonly startedMonth: string | null;
  readonly finishedMonth: string | null;
  readonly score: number | null;
  readonly isGoty: boolean;
  readonly body: string | null;
};

// Edición: la identidad es inmutable, así que no viaja.
export type ReviewUpdateRequest = Omit<ReviewCreateRequest, "gameId" | "platform">;

function isRecord(value: unknown): value is UnknownRecord {
  return typeof value === "object" && value !== null;
}

// El backend responde camelCase; se tolera PascalCase porque los DTOs de .NET pueden reconfigurarse.
function read(value: UnknownRecord, key: string): unknown {
  return value[key] ?? value[key.charAt(0).toUpperCase() + key.slice(1)];
}

function has(value: UnknownRecord, key: string): boolean {
  return key in value || key.charAt(0).toUpperCase() + key.slice(1) in value;
}

function toText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

// Identidad: solo un entero positivo. Un texto o un decimal no es un id.
function toPositiveInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : null;
}

// La nota solo se acepta como entero 0..100. Un decimal, un texto o un rango fuera de la banda se
// descartan a `null`: nunca se redondea ni se recorta para "arreglar" el dato.
function toScore(value: unknown): number | null {
  return typeof value === "number" && Number.isInteger(value) && value >= 0 && value <= 100 ? value : null;
}

// Mes estricto `YYYY-MM`, con mes 01..12. `2026-1`, `2026-13` o `2026/01` no son meses.
const REVIEW_MONTH = /^(\d{4})-(\d{2})$/;

function toReviewMonth(value: unknown): string | null {
  const text = toText(value);
  if (text === null) return null;
  const match = REVIEW_MONTH.exec(text);
  if (match === null) return null;
  const month = Number(match[2]);
  return month >= 1 && month <= 12 ? text : null;
}

// Solo ISO-8601 con hora y zona, igual que el resto de los contratos.
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

function toIsoDateTime(value: unknown): string | null {
  const text = toText(value);
  if (text === null || !ISO_DATE_TIME.test(text)) return null;
  const day = text.slice(0, 10);
  const calendar = new Date(`${day}T00:00:00Z`);
  if (Number.isNaN(calendar.getTime()) || calendar.toISOString().slice(0, 10) !== day) return null;
  return Number.isNaN(new Date(text).getTime()) ? null : text;
}

// El texto es libre; se acota para que un payload abusivo no infle la respuesta ni el render. Un cuerpo
// vacío o solo espacios es "sin texto", no una cadena vacía pintada.
const MAX_BODY_LENGTH = 20_000;

function toBody(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const text = value.trim();
  return text === "" ? null : text.slice(0, MAX_BODY_LENGTH);
}

/**
 * Reseña válida o `null`. La identidad (`reviewId`, `gameId`, `platform`) es obligatoria: sin ella no
 * existe reseña y se descarta en vez de inventar una. Todo lo demás es opcional y degrada a `null` /
 * `false`.
 */
export function normalizeReview(value: unknown): Review | null {
  if (!isRecord(value)) return null;

  const reviewId = toPositiveInteger(read(value, "reviewId"));
  const gameId = toPositiveInteger(read(value, "gameId"));
  const platform = toStoreKey(read(value, "platform"));
  if (reviewId === null || gameId === null || platform === null) return null;

  return {
    reviewId,
    gameId,
    platform,
    startedMonth: toReviewMonth(read(value, "startedMonth")),
    finishedMonth: toReviewMonth(read(value, "finishedMonth")),
    score: toScore(read(value, "score")),
    scoreLabel: toText(read(value, "scoreLabel")),
    // Solo el literal `true` marca GOTY: `"true"` o `1` se quedan en `false`.
    isGoty: read(value, "isGoty") === true,
    body: toBody(read(value, "body")),
    created: toIsoDateTime(read(value, "created")),
    updated: toIsoDateTime(read(value, "updated"))
  };
}

// Tope defensivo: un juego puede tener muchas reseñas (una por partida) y el bucle no debe crecer sin
// límite con un payload abusivo.
const MAX_REVIEWS = 500;

/**
 * Lista tolerante: ausencia o forma rara dan arreglo vacío, y cada entrada inválida se descarta. Es la
 * forma que consumen los campos aditivos (listado de biblioteca y detalle del juego), donde un payload
 * viejo sin reseñas debe seguir funcionando.
 */
export function normalizeReviewList(input: unknown): readonly Review[] {
  if (!Array.isArray(input)) return [];

  const reviews: Review[] = [];
  for (const entry of input) {
    const review = normalizeReview(entry);
    if (review === null) continue;
    reviews.push(review);
    if (reviews.length === MAX_REVIEWS) break;
  }
  return reviews;
}

/**
 * Lista de un endpoint dedicado (`GET /api/reviews`). A diferencia de `normalizeReviewList`, un cuerpo
 * que no es arreglo es `null` (respuesta inválida), no "sin reseñas": una forma rota no puede leerse
 * como una lista vacía.
 */
export function normalizeReviewListResponse(input: unknown): readonly Review[] | null {
  return Array.isArray(input) ? normalizeReviewList(input) : null;
}

// Campos de escritura. Un campo ausente o `null` es válido (se limpia); un campo presente con una forma
// inválida rechaza la petición entera en vez de guardar en silencio un valor distinto al que se escribió.
function parseWriteFields(record: UnknownRecord): ReviewUpdateRequest | null {
  let startedMonth: string | null = null;
  if (has(record, "startedMonth") && read(record, "startedMonth") !== null) {
    startedMonth = toReviewMonth(read(record, "startedMonth"));
    if (startedMonth === null) return null;
  }

  let finishedMonth: string | null = null;
  if (has(record, "finishedMonth") && read(record, "finishedMonth") !== null) {
    finishedMonth = toReviewMonth(read(record, "finishedMonth"));
    if (finishedMonth === null) return null;
  }

  let score: number | null = null;
  if (has(record, "score") && read(record, "score") !== null) {
    score = toScore(read(record, "score"));
    if (score === null) return null;
  }

  let isGoty = false;
  if (has(record, "isGoty") && read(record, "isGoty") !== null) {
    if (typeof read(record, "isGoty") !== "boolean") return null;
    isGoty = read(record, "isGoty") as boolean;
  }

  let body: string | null = null;
  if (has(record, "body") && read(record, "body") !== null) {
    if (typeof read(record, "body") !== "string") return null;
    body = toBody(read(record, "body"));
  }

  return { startedMonth, finishedMonth, score, isGoty, body };
}

/** Alta validada de reseña, o `null` si la identidad o algún campo presente no cumple el contrato. */
export function parseReviewCreateRequest(input: unknown): ReviewCreateRequest | null {
  if (!isRecord(input)) return null;

  const gameId = toPositiveInteger(read(input, "gameId"));
  const platform = toStoreKey(read(input, "platform"));
  if (gameId === null || platform === null) return null;

  const fields = parseWriteFields(input);
  return fields === null ? null : { gameId, platform, ...fields };
}

/** Edición validada de reseña (sin identidad), o `null` si algún campo presente no cumple el contrato. */
export function parseReviewUpdateRequest(input: unknown): ReviewUpdateRequest | null {
  return isRecord(input) ? parseWriteFields(input) : null;
}

// Marca temporal de una reseña: `updated` manda sobre `created`. Una fecha ausente o ilegible cuenta como
// la más antigua posible, nunca descarta la reseña.
function reviewStamp(review: Review): number {
  const parsed = Date.parse(review.updated ?? review.created ?? "");
  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * La reseña que representa a un juego: la última escrita. Un juego puede tener varias reseñas de la misma
 * plataforma (una por partida) y la grilla solo pinta una etiqueta, así que necesita una elegida. Los
 * empates se rompen por `reviewId` para que la elección sea determinista; una lista vacía no tiene
 * representante.
 */
export function newestReview(reviews: readonly Review[]): Review | null {
  return reviews.reduce<Review | null>((best, candidate) => {
    if (best === null) return candidate;
    const candidateStamp = reviewStamp(candidate);
    const bestStamp = reviewStamp(best);
    if (candidateStamp > bestStamp) return candidate;
    return candidateStamp === bestStamp && candidate.reviewId > best.reviewId ? candidate : best;
  }, null);
}

// Mes `YYYY-MM` legible ("mar 2026"). Es la única forma en que la UI muestra un mes de reseña.
const monthLabelFormatter = new Intl.DateTimeFormat("es-MX", { month: "short", year: "numeric", timeZone: "UTC" });

export function formatReviewMonth(value: string | null): string | null {
  if (value === null) return null;
  const date = new Date(`${value}-01T00:00:00Z`);
  return Number.isNaN(date.getTime()) ? null : monthLabelFormatter.format(date);
}
