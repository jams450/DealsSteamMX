import { Home, Search, Users } from "lucide-react";

export type NavItem = {
  href: string;
  label: string;
  icon: typeof Users;
};

export const productNavItems: NavItem[] = [
  { href: "/", label: "Inicio", icon: Home },
  { href: "/search", label: "Comparar", icon: Search }
];

export const adminNavItems: NavItem[] = [
  { href: "/users", label: "Usuarios", icon: Users }
];

export const appNavItems: NavItem[] = [...productNavItems, ...adminNavItems];

export function isRouteActive(pathname: string, href: string) {
  return pathname === href || pathname.startsWith(`${href}/`);
}
