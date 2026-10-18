/**
 * Contrato de consulta del buscador general (`/search`). La cadena front → BFF → API ya está intacta;
 * este módulo extrae la parte determinista para dejar una regresión escrita: lo que viaja al backend es
 * la consulta recortada, y una consulta por debajo del mínimo no viaja.
 */

export const MIN_QUERY_LENGTH = 2;
export const SUGGESTION_DEBOUNCE_MS = 300;
export const SEARCH_MIN_LENGTH_MESSAGE = `Escribe al menos ${MIN_QUERY_LENGTH} caracteres.`;

export type SearchRequestResolution = {
  /** Consulta ya recortada; es la que se envía al backend cuando no hay error. */
  readonly query: string;
  /** Mensaje de validación, o `null` cuando la consulta es utilizable. */
  readonly error: string | null;
};

export function resolveSearchRequest(value: string): SearchRequestResolution {
  const query = value.trim();
  return query.length >= MIN_QUERY_LENGTH
    ? { query, error: null }
    : { query, error: SEARCH_MIN_LENGTH_MESSAGE };
}

/** Las sugerencias se piden mientras se escribe (≥ mínimo) y se apagan para el término ya buscado. */
export function shouldSuggest(trimmedQuery: string, searchedQuery: string | null): boolean {
  return trimmedQuery.length >= MIN_QUERY_LENGTH && trimmedQuery !== searchedQuery;
}
