import { Activity, Bookmark, Compass, GitMerge, Home, Library, Package, Search, Trophy, Users } from "lucide-react";

export type NavLink = {
  href: string;
  label: string;
  icon: typeof Users;
};

/**
 * Un grupo del riel izquierdo. La barra superior se saturaba al juntar doce enlaces en
 * una fila; en vertical el grupo es la jerarquía y no hace falta un dropdown por rama.
 * `MAX_NAV_GROUP_ITEMS` es el freno: un grupo que crece más que eso vuelve a leerse como
 * una lista plana y deja de agrupar nada.
 */
export type NavGroup = {
  label: string;
  items: readonly NavLink[];
};

export const MAX_NAV_GROUP_ITEMS = 6;

export const navGroups: readonly NavGroup[] = [
  {
    label: "Explorar",
    items: [
      { href: "/", label: "Inicio", icon: Home },
      { href: "/search", label: "Comparar", icon: Search },
      { href: "/discover", label: "Descubrir", icon: Compass },
      { href: "/deals", label: "Ofertas globales", icon: Compass }
    ]
  },
  {
    label: "Tu colección",
    items: [
      { href: "/wishlist", label: "Wishlist", icon: Bookmark },
      { href: "/bundles", label: "Bundles", icon: Package },
      { href: "/library", label: "Biblioteca", icon: Library },
      { href: "/library/tiers", label: "Tier list", icon: Trophy }
    ]
  },
  {
    label: "Catálogo",
    items: [
      { href: "/library/duplicates", label: "Duplicados", icon: GitMerge },
      { href: "/library/reconciliation", label: "Reconciliación", icon: GitMerge }
    ]
  },
  {
    label: "Administración",
    items: [
      { href: "/users", label: "Usuarios", icon: Users },
      { href: "/jobs", label: "Jobs", icon: Activity }
    ]
  }
];

/** Fuente única: toda ruta del producto sale de los grupos, nunca de una lista paralela. */
export const appNavItems: readonly NavLink[] = navGroups.flatMap((group) => group.items);

const NAV_HREFS: readonly string[] = appNavItems.map((item) => item.href);

// Una ruta que es prefijo de otra solo se marca activa por coincidencia exacta: en
// `/library/duplicates`, «Biblioteca» y «Duplicados» no pueden estar activas a la vez.
export function isRouteActive(pathname: string, href: string) {
  if (pathname === href) return true;
  const hasNested = NAV_HREFS.some((other) => other !== href && other.startsWith(`${href}/`));
  return !hasNested && pathname.startsWith(`${href}/`);
}

/**
 * Índice que debe recibir el foco cuando `Tab`/`Shift+Tab` sale de la lista enfocable del
 * drawer, o `null` cuando el navegador ya hace lo correcto.
 *
 * Vive aquí, junto a la navegación, y no en `shell-chrome.tsx`, por una razón concreta:
 * `node --test` no puede importar un `.tsx` (el type stripping no entiende JSX), y la
 * trampa de foco es justo la parte del drawer que hay que poder probar de forma
 * determinista. Los dos shells consumen esta misma función, así que no hay dos traps que
 * se desincronicen.
 */
export function nextFocusIndex(index: number, total: number, shiftKey: boolean): number | null {
  if (total <= 0) return null;
  if (index < 0) return shiftKey ? total - 1 : 0;
  if (index >= total) return shiftKey ? total - 1 : 0;
  if (shiftKey) return index === 0 ? total - 1 : null;
  return index === total - 1 ? 0 : null;
}
