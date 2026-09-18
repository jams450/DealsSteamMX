// Vocabulario de tiendas compartido por la biblioteca (`user_library.store`) y los badges de posesión
// del detalle de juego. Es la única fuente de los nombres visibles y de la lista canónica de llaves:
// ninguna pantalla inventa su propio mapa.
export const STORE_KEYS = ["steam", "epic", "gog", "xbox", "amazon", "ubisoft", "humble", "battlenet"] as const;

export type StoreKey = (typeof STORE_KEYS)[number];

// Alias que ya están vivos en los datos: la biblioteca normaliza su columna `store` a minúsculas y el
// export de Playnite usa grafías como "Ubisoft Connect" y "Battle.net". Lo que no está en la tabla no es
// una llave canónica.
const STORE_ALIASES: Readonly<Record<string, StoreKey>> = {
  steam: "steam",
  epic: "epic",
  "epic games": "epic",
  gog: "gog",
  xbox: "xbox",
  amazon: "amazon",
  ubisoft: "ubisoft",
  "ubisoft connect": "ubisoft",
  humble: "humble",
  battlenet: "battlenet",
  "battle.net": "battlenet"
};

// Llave canónica de tienda: minúscula, dentro de la lista, con alias conocidos mapeados. Cualquier otra
// cosa es null, y un null se descarta: una tienda sin nombre no puede ser "GOG" ni nada.
export function toStoreKey(value: unknown): StoreKey | null {
  if (typeof value !== "string") return null;
  return STORE_ALIASES[value.trim().toLowerCase()] ?? null;
}

const STORE_LABELS: Readonly<Record<string, string>> = {
  steam: "Steam",
  epic: "Epic Games",
  gog: "GOG",
  xbox: "Xbox",
  amazon: "Amazon",
  ubisoft: "Ubisoft Connect",
  humble: "Humble",
  battlenet: "Battle.net",
  // Grafías que la biblioteca ya guarda: reconocidas para pintar el mismo nombre que en su filtro.
  "ubisoft connect": "Ubisoft Connect",
  "battle.net": "Battle.net"
};

// Nombre visible de una tienda. El valor normalizado de la biblioteca tolera textos ajenos al catálogo
// (los manda el export de Playnite): una tienda no reconocida se muestra con su propio texto, nunca se
// oculta ni se renombra.
export function storeLabel(store: string): string {
  return STORE_LABELS[store] ?? store;
}
