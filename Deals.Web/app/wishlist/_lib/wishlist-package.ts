// Selección del paquete de wishlist. Módulo puro, sin imports: `node --test` lo ejecuta tal cual (Node v26
// quita los tipos) y no arrastra React ni el contrato del BFF. La aritmética del paquete NO vive aquí: el
// subtotal lo calcula el servidor, porque sumar precios ya cargados en el cliente convierte una fila vieja
// en un total que parece fresco.

// Tope del preview. El backend lo impone otra vez (400 por encima); esto solo evita que la UI construya una
// petición que ya sabe que va a fallar.
export const MAX_PACKAGE_APP_IDS = 200;

// Identidad de la selección: el AppID, nunca el índice de fila. Ordenar, filtrar o paginar mueve las filas,
// así que un índice terminaría apuntando a otro juego.
export function toggleAppId(
  selected: ReadonlySet<number>,
  appId: number,
  checked: boolean
): ReadonlySet<number> {
  const next = new Set(selected);
  if (checked) {
    next.add(appId);
  } else {
    next.delete(appId);
  }
  return next;
}

// Marca o desmarca un lote (la página visible, desde el checkbox de cabecera).
export function setAppIds(
  selected: ReadonlySet<number>,
  appIds: readonly number[],
  checked: boolean
): ReadonlySet<number> {
  const next = new Set(selected);
  for (const appId of appIds) {
    if (checked) {
      next.add(appId);
    } else {
      next.delete(appId);
    }
  }
  return next;
}

export type PageSelectionState = "all" | "some" | "none";

// Estado del checkbox de cabecera: `some` es el estado indeterminado. Una página sin filas es `none`.
export function pageSelectionState(
  selected: ReadonlySet<number>,
  pageAppIds: readonly number[]
): PageSelectionState {
  if (pageAppIds.length === 0) return "none";
  let selectedOnPage = 0;
  for (const appId of pageAppIds) {
    if (selected.has(appId)) selectedOnPage++;
  }
  if (selectedOnPage === 0) return "none";
  return selectedOnPage === pageAppIds.length ? "all" : "some";
}

export interface ReconcileResult {
  // La misma referencia cuando no hay nada que quitar: el llamador puede comparar por identidad y evitar
  // un render de más.
  readonly selection: ReadonlySet<number>;
  readonly removed: readonly number[];
}

// Un juego puede desaparecer de la wishlist entre la selección y el preview (el sync lo sacó, el servidor
// no lo reconoció). La selección se poda contra lo que la lista tiene hoy; lo que se quitó se reporta para
// poder avisar en vez de encoger el total en silencio.
export function reconcileAppIds(
  selected: ReadonlySet<number>,
  knownAppIds: ReadonlySet<number>
): ReconcileResult {
  const removed: number[] = [];
  for (const appId of selected) {
    if (!knownAppIds.has(appId)) removed.push(appId);
  }

  if (removed.length === 0) return { selection: selected, removed };

  const next = new Set(selected);
  for (const appId of removed) next.delete(appId);
  return { selection: next, removed };
}

// AppIDs que viajan al preview: la selección completa, no solo la página visible, y ordenados para que la
// petición sea estable y comparable en logs.
export function packageRequestAppIds(selected: ReadonlySet<number>): number[] {
  return [...selected].sort((left, right) => left - right);
}

export function exceedsPackageLimit(selected: ReadonlySet<number>): boolean {
  return selected.size > MAX_PACKAGE_APP_IDS;
}
