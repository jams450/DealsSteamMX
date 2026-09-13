import { Users } from "lucide-react";

type NavItem = {
  href: string;
  label: string;
  icon: typeof Users;
};

export const appNavItems: NavItem[] = [{ href: "/users", label: "Usuarios", icon: Users }];

export function isRouteActive(pathname: string, href: string) {
  return pathname === href || pathname.startsWith(`${href}/`);
}
