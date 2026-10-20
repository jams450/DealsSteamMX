"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { cn } from "@/lib/ui/cn";
import { isRouteActive, navGroups } from "./nav-config";

type AppNavigationProps = {
  /** `aria-label` del landmark. El riel y el drawer usan nombres distintos. */
  label: string;
  /** Se llama al elegir un destino, para que el drawer móvil se cierre. */
  onNavigate?: () => void;
  className?: string;
};

/**
 * Navegación vertical agrupada, compartida por el riel de escritorio y el drawer móvil:
 * el mismo markup en los dos lugares, así que un destino no puede existir en un ancho y
 * desaparecer en el otro. Un solo `<nav>` por instancia visible.
 */
export function AppNavigation({ label, onNavigate, className }: AppNavigationProps) {
  const pathname = usePathname();

  return (
    <nav aria-label={label} className={cn("flex min-w-0 flex-col gap-5", className)}>
      {navGroups.map((group) => {
        const headingId = `nav-group-${group.label.toLowerCase().replaceAll(" ", "-")}`;
        return (
          <div key={group.label} className="min-w-0">
            <p id={headingId} className="px-2 pb-1 text-xs font-semibold uppercase tracking-widest text-muted">
              {group.label}
            </p>
            <ul aria-labelledby={headingId} className="flex min-w-0 flex-col gap-0.5">
              {group.items.map((item) => {
                const Icon = item.icon;
                const active = isRouteActive(pathname, item.href);
                return (
                  <li key={item.href} className="min-w-0">
                    <Link
                      href={item.href}
                      onClick={onNavigate}
                      aria-current={active ? "page" : undefined}
                      className={cn(
                        "flex h-10 min-w-0 items-center gap-2.5 rounded-[var(--radius-sm)] border-l-2 px-2.5 text-sm font-semibold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                        active
                          ? "border-l-[color:var(--color-accent)] bg-[var(--color-accent-soft)] text-primary"
                          : "border-l-transparent text-secondary hover:border-l-[color:var(--color-accent)] hover:bg-[var(--color-accent-soft)] hover:text-primary"
                      )}
                    >
                      <Icon className="h-4 w-4 shrink-0" aria-hidden="true" />
                      <span className="truncate">{item.label}</span>
                    </Link>
                  </li>
                );
              })}
            </ul>
          </div>
        );
      })}
    </nav>
  );
}
