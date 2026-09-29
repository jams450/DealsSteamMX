"use client";

import { ChevronDown } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { cn } from "@/lib/ui/cn";
import { appNavItems, isNavMenu, isRouteActive, type NavLink } from "./nav-config";

type AppNavigationProps = {
  mobile?: boolean;
  onNavigate?: () => void;
  getItemClassName: (active: boolean) => string;
};

export function AppNavigation({ mobile = false, onNavigate, getItemClassName }: AppNavigationProps) {
  const pathname = usePathname();
  const [openMenu, setOpenMenu] = useState<string | null>(null);
  const menuRefs = useRef(new Map<string, HTMLDivElement>());
  const triggerRefs = useRef(new Map<string, HTMLButtonElement>());
  const pendingFocusRef = useRef<{ label: string; position: "first" | "last" } | null>(null);

  useEffect(() => {
    if (!openMenu) return;

    const pendingFocus = pendingFocusRef.current;
    if (pendingFocus?.label === openMenu) {
      const menuItems = menuRefs.current.get(openMenu)?.querySelectorAll<HTMLAnchorElement>('[role="menuitem"]');
      const target = pendingFocus.position === "first" ? menuItems?.[0] : menuItems?.[menuItems.length - 1];
      target?.focus();
      pendingFocusRef.current = null;
    }

    const onDocumentPointerDown = (event: PointerEvent) => {
      if (![...menuRefs.current.values()].some((menu) => menu.contains(event.target as Node))) setOpenMenu(null);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setOpenMenu(null);
        triggerRefs.current.get(openMenu)?.focus();
      }
    };

    document.addEventListener("pointerdown", onDocumentPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onDocumentPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [openMenu]);

  function openMenuWithFocus(label: string, position: "first" | "last" = "first") {
    pendingFocusRef.current = { label, position };
    setOpenMenu(label);
  }

  function onMenuKeyDown(event: ReactKeyboardEvent<HTMLDivElement>, items: readonly NavLink[]) {
    const menuItems = Array.from(event.currentTarget.querySelectorAll<HTMLAnchorElement>('[role="menuitem"]'));
    const index = menuItems.indexOf(document.activeElement as HTMLAnchorElement);

    if (event.key === "ArrowDown") {
      event.preventDefault();
      menuItems[(index + 1) % menuItems.length]?.focus();
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      menuItems[(index - 1 + menuItems.length) % menuItems.length]?.focus();
    } else if (event.key === "Home") {
      event.preventDefault();
      menuItems[0]?.focus();
    } else if (event.key === "End") {
      event.preventDefault();
      menuItems[items.length - 1]?.focus();
    }
  }

  return (
    <nav
      aria-label={mobile ? "Navegación móvil principal" : "Navegación principal"}
      className={cn("flex items-center gap-1 overflow-visible", mobile && "grid items-stretch gap-1 sm:grid-cols-2")}
    >
      {appNavItems.map((item) => {
        if (!isNavMenu(item)) {
          const Icon = item.icon;
          const active = isRouteActive(pathname, item.href);
          return (
            <Link
              key={item.href}
              href={item.href}
              onClick={onNavigate}
              aria-current={active ? "page" : undefined}
              className={getItemClassName(active)}
            >
              <Icon className="mr-2 h-4 w-4 shrink-0" aria-hidden="true" />
              {item.label}
            </Link>
          );
        }

        const active = item.items.some((child) => isRouteActive(pathname, child.href));
        const open = openMenu === item.label;
        const menuId = `app-navigation-${item.label.toLowerCase().replaceAll(" ", "-")}`;

        return (
          <div
            key={item.label}
            ref={(element) => {
              if (element) menuRefs.current.set(item.label, element);
              else menuRefs.current.delete(item.label);
            }}
            className={cn("relative", mobile && "min-w-0")}
          >
            <button
              ref={(element) => {
                if (element) triggerRefs.current.set(item.label, element);
                else triggerRefs.current.delete(item.label);
              }}
              type="button"
              className={cn(getItemClassName(active), "w-full")}
              aria-current={active ? "page" : undefined}
              aria-expanded={open}
              aria-controls={menuId}
              aria-haspopup="menu"
              onClick={() => setOpenMenu((current) => (current === item.label ? null : item.label))}
              onKeyDown={(event) => {
                if (event.key === "ArrowDown") {
                  event.preventDefault();
                  openMenuWithFocus(item.label);
                } else if (event.key === "ArrowUp") {
                  event.preventDefault();
                  openMenuWithFocus(item.label, "last");
                }
              }}
            >
              <item.icon className="mr-2 h-4 w-4 shrink-0" aria-hidden="true" />
              {item.label}
              <ChevronDown className={cn("ml-1 h-4 w-4 shrink-0 transition-transform", open && "rotate-180")} aria-hidden="true" />
            </button>
            {open ? (
              <div
                id={menuId}
                role="menu"
                aria-label={item.label}
                onKeyDown={(event) => onMenuKeyDown(event, item.items)}
                className={cn(
                  "z-30 mt-1 min-w-48 rounded-[var(--radius-md)] border border-strong bg-[var(--color-surface-1)] p-1 shadow-[var(--shadow-md)]",
                  mobile ? "relative w-full" : "absolute left-0"
                )}
              >
                {item.items.map((child) => {
                  const Icon = child.icon;
                  const childActive = isRouteActive(pathname, child.href);
                  return (
                    <Link
                      key={child.href}
                      href={child.href}
                      role="menuitem"
                      onClick={() => {
                        setOpenMenu(null);
                        onNavigate?.();
                      }}
                      aria-current={childActive ? "page" : undefined}
                      className={cn(
                        "flex h-10 items-center rounded-[var(--radius-sm)] px-3 text-sm font-semibold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                        childActive
                          ? "bg-[var(--color-accent-soft)] text-primary"
                          : "text-secondary hover:bg-[var(--color-accent-soft)] hover:text-primary"
                      )}
                    >
                      <Icon className="mr-2 h-4 w-4 shrink-0" aria-hidden="true" />
                      {child.label}
                    </Link>
                  );
                })}
              </div>
            ) : null}
          </div>
        );
      })}
    </nav>
  );
}
