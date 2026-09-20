import type { WishlistItem } from "./wishlist-contract";

/**
 * Los cinco proveedores que un juego de la wishlist sincroniza por separado, en el orden en que se leen.
 * Cada uno tiene su propio sello en el snapshot: un proveedor caído conserva su fecha vieja mientras los
 * demás avanzan, y eso es lo que la columna «Sincronización» existe para mostrar.
 *
 * El mapeo clave → campo vive aquí y no en el componente porque un nombre mal escrito no falla: deja la
 * celda en «—» y la fila parece simplemente sin sincronizar. `wishlist-sync.test.ts` lo fija.
 */
export const SYNC_STORES = [
  { key: "steam", label: "Steam", field: "steamSyncedAt" },
  { key: "itad", label: "ITAD", field: "itadSyncedAt" },
  { key: "ggdeals", label: "GG.deals", field: "ggDealsSyncedAt" },
  { key: "epic", label: "Epic", field: "epicSyncedAt" },
  { key: "microsoft", label: "MS", field: "microsoftSyncedAt" }
] as const satisfies readonly { key: string; label: string; field: keyof WishlistItem }[];

export type SyncStore = (typeof SYNC_STORES)[number];
export type SyncStoreKey = SyncStore["key"];

export function syncStamp(item: WishlistItem, store: SyncStore): string | null {
  return item[store.field];
}

/** Sello más reciente de los cinco, para ordenar la columna. `undefined` (nunca `null`) deja los juegos
 * sin ninguna sincronización al final en las dos direcciones. */
export function latestSyncTime(item: WishlistItem): number | undefined {
  const times = SYNC_STORES
    .map((store) => syncStamp(item, store))
    .filter((stamp): stamp is string => Boolean(stamp))
    .map((stamp) => new Date(stamp).getTime())
    .filter((time) => !Number.isNaN(time));

  return times.length > 0 ? Math.max(...times) : undefined;
}
