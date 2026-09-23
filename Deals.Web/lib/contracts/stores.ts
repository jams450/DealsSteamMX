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

// Slug de plataforma abierta (consola): espejo de `StoreKeys.Normalize` del backend
// (docs/PLAN_CONSOLE.md §3). Cualquier consola entra sin tocar código.
const PLATFORM_SLUG = /^[a-z0-9][a-z0-9._-]{1,31}$/;

// Llave canónica de cualquier fila de biblioteca o reseña: alias de tienda conocido → su llave;
// texto que sea slug de plataforma → minúsculas; lo demás → null. Acepta consolas nuevas, a
// diferencia de `toStoreKey`, que sigue cerrado para los sitios que solo hablan de las ocho tiendas.
export function normalizeStore(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const text = value.trim().toLowerCase();
  if (text.length === 0) return null;
  return STORE_ALIASES[text] ?? (PLATFORM_SLUG.test(text) ? text : null);
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
  "battle.net": "Battle.net",
  // Plataformas de consola del alta manual (catálogo visible de PLATFORM_CATALOG_KEYS): solo pintura —
  // una plataforma ausente de esta lista se muestra con su propio slug, nunca se oculta.
  switch: "Nintendo Switch",
  ps5: "PlayStation 5",
  ps4: "PlayStation 4",
  ps3: "PlayStation 3",
  ps2: "PlayStation 2",
  ps1: "PlayStation",
  "ps-vita": "PlayStation Vita",
  psp: "PSP",
  "xbox-one": "Xbox One",
  xbox360: "Xbox 360",
  "series-s": "Xbox Series S",
  "series-x": "Xbox Series X",
  "3ds": "Nintendo 3DS",
  ds: "Nintendo DS",
  wii: "Wii",
  wiiu: "Wii U",
  gamecube: "Nintendo GameCube",
  n64: "Nintendo 64",
  snes: "Super Nintendo",
  nes: "NES",
  dreamcast: "Sega Dreamcast"
};

// Orden del selector de consolas del alta manual (docs/PLAN_CONSOLE.md §5). Cosmético: la validación es
// `normalizeStore`, así que una consola que falte aquí se escribe en «Otra plataforma…» sin tocar código.
export const PLATFORM_CATALOG_KEYS = [
  "switch",
  "ps5",
  "ps4",
  "ps3",
  "ps2",
  "ps1",
  "ps-vita",
  "psp",
  "xbox-one",
  "xbox360",
  "series-s",
  "series-x",
  "3ds",
  "ds",
  "wii",
  "wiiu",
  "gamecube",
  "n64",
  "snes",
  "nes",
  "dreamcast"
] as const;

// Nombre visible de una tienda. El valor normalizado de la biblioteca tolera textos ajenos al catálogo
// (los manda el export de Playnite): una tienda no reconocida se muestra con su propio texto, nunca se
// oculta ni se renombra.
export function storeLabel(store: string): string {
  return STORE_LABELS[store] ?? store;
}
