/**
 * Programación de la búsqueda «mientras escribes» de la wishlist.
 *
 * El defecto original era de cableado, no de backend: la toolbar escribía `draftFilters.search` y la
 * petición leía `appliedFilters`, así que teclear no cambiaba la consulta enviada. Aquí vive la parte
 * pura y determinista del arreglo: un programador con reloj inyectable que decide *cuándo* se aplica la
 * búsqueda. El componente solo aporta los temporizadores reales y los efectos sobre el estado.
 *
 * Reglas:
 * - Escribir aplica tras un retardo breve (debounce), con la última tecla como valor.
 * - Enter aplica de inmediato y cancela lo pendiente.
 * - Limpiar aplica vacío de inmediato y cancela lo pendiente (nunca deja pasar el borrador viejo).
 * - El commit solo toca `search`; los demás filtros aplicados se conservan y los del borrador del panel
 *   no se arrastran (`withAppliedSearch`).
 */

export const WISHLIST_SEARCH_DEBOUNCE_MS = 300;

/** Temporizadores inyectables para poder probar el debounce con un reloj de mentira. */
export interface WishlistSearchTimer {
  setTimeout(handler: () => void, timeoutMs: number): number;
  clearTimeout(handle: number): void;
}

export interface WishlistSearchScheduler {
  /** El usuario escribió: cancela el pendiente y programa el commit tras el retardo breve. */
  change(value: string): void;
  /** El usuario confirmó con Enter: aplica ya, sin esperar. */
  submit(value: string): void;
  /** El usuario limpió el campo: aplica vacío ya, sin esperar. */
  clear(): void;
  /** Cancela el commit pendiente sin aplicarlo (desmontaje o cuando otro control aplica los filtros). */
  dispose(): void;
}

export interface WishlistSearchSchedulerOptions {
  readonly timer: WishlistSearchTimer;
  readonly onCommit: (search: string) => void;
  readonly debounceMs?: number;
}

export function createWishlistSearchScheduler({
  timer,
  onCommit,
  debounceMs = WISHLIST_SEARCH_DEBOUNCE_MS
}: WishlistSearchSchedulerOptions): WishlistSearchScheduler {
  let pending: number | null = null;

  function cancelPending() {
    if (pending === null) return;
    timer.clearTimeout(pending);
    pending = null;
  }

  return {
    change(value) {
      cancelPending();
      pending = timer.setTimeout(() => {
        pending = null;
        onCommit(value);
      }, debounceMs);
    },
    submit(value) {
      cancelPending();
      onCommit(value);
    },
    clear() {
      cancelPending();
      onCommit("");
    },
    dispose() {
      cancelPending();
    }
  };
}

/**
 * Aplica una búsqueda sobre los filtros ya aplicados. El commit en vivo nunca toma el borrador
 * completo: solo cambia `search`, así que una edición pendiente del panel (precio, compra,
 * categorías) sigue esperando a «Aplicar filtros».
 */
export function withAppliedSearch<T extends { readonly search: string }>(filters: T, search: string): T {
  return { ...filters, search };
}
