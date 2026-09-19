import { Bookmark, GitMerge, Home, Library, Search, Users } from "lucide-react";

export type NavItem = {
  href: string;
  label: string;
  icon: typeof Users;
};

export const productNavItems: NavItem[] = [
  { href: "/", label: "Inicio", icon: Home },
  { href: "/search", label: "Comparar", icon: Search },
  { href: "/wishlist", label: "Wishlist", icon: Bookmark },
  { href: "/library", label: "Biblioteca", icon: Library },
  // Mantenimiento del catálogo: separado de la Biblioteca porque fusionar es irreversible.
  { href: "/library/duplicates", label: "Duplicados", icon: GitMerge }
];

export const adminNavItems: NavItem[] = [
  { href: "/users", label: "Usuarios", icon: Users }
];

export const appNavItems: NavItem[] = [...productNavItems, ...adminNavItems];

const NAV_HREFS: readonly string[] = appNavItems.map((item) => item.href);

// Una ruta que es prefijo de otra solo se marca activa por coincidencia exacta: en
// `/library/duplicates`, «Biblioteca» y «Duplicados» no pueden estar activas a la vez.
export function isRouteActive(pathname: string, href: string) {
  if (pathname === href) return true;
  const hasNested = NAV_HREFS.some((other) => other !== href && other.startsWith(`${href}/`));
  return !hasNested && pathname.startsWith(`${href}/`);
}
