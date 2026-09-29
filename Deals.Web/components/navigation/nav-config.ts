import { Bookmark, Compass, GitMerge, Home, Library, Search, Users, Activity } from "lucide-react";

export type NavLink = {
  href: string;
  label: string;
  icon: typeof Users;
};

export type NavMenu = {
  label: string;
  icon: typeof Users;
  items: readonly NavLink[];
};

export type NavItem = NavLink | NavMenu;

export function isNavMenu(item: NavItem): item is NavMenu {
  return "items" in item;
}

export const productNavItems: NavItem[] = [
  { href: "/", label: "Inicio", icon: Home },
  { href: "/search", label: "Comparar", icon: Search },
  { href: "/discover", label: "Descubrir", icon: Compass },
  { href: "/wishlist", label: "Wishlist", icon: Bookmark },
  { href: "/library", label: "Biblioteca", icon: Library },
  {
    label: "Merge",
    icon: GitMerge,
    items: [
      { href: "/library/duplicates", label: "Duplicados", icon: GitMerge },
      { href: "/library/reconciliation", label: "Reconciliación", icon: GitMerge }
    ]
  }
];

export const adminNavItems: NavItem[] = [
  { href: "/users", label: "Usuarios", icon: Users },
  { href: "/jobs", label: "Jobs", icon: Activity }
];

export const appNavItems: NavItem[] = [...productNavItems, ...adminNavItems];

const NAV_HREFS: readonly string[] = appNavItems.flatMap((item) => (isNavMenu(item) ? item.items.map((child) => child.href) : item.href));

// Una ruta que es prefijo de otra solo se marca activa por coincidencia exacta: en
// `/library/duplicates`, «Biblioteca» y «Duplicados» no pueden estar activas a la vez.
export function isRouteActive(pathname: string, href: string) {
  if (pathname === href) return true;
  const hasNested = NAV_HREFS.some((other) => other !== href && other.startsWith(`${href}/`));
  return !hasNested && pathname.startsWith(`${href}/`);
}
