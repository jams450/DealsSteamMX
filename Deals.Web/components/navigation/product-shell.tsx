"use client";

import { useRef, useState, type ReactNode } from "react";
import { ThemeToggle } from "@/components/theme/theme-toggle";
import { AppNavigation } from "./app-navigation";
import { ShellDrawer, ShellHeader, ShellRail } from "./shell-chrome";

type ProductShellProps = {
  title: string;
  subtitle?: string;
  meta?: ReactNode;
  children: ReactNode;
};

const DRAWER_ID = "product-navigation-drawer";

/**
 * Superficie de producto. La navegación vive en el riel izquierdo (escritorio) y en el drawer
 * (móvil); el header solo lleva título, meta y controles, así que ninguna pantalla pequeña
 * amontona opciones en una fila.
 */
export function ProductShell({ title, subtitle, meta, children }: ProductShellProps) {
  const [mobileOpen, setMobileOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);

  return (
    <main className="app-page">
      <div className="flex w-full items-start">
        <ShellRail
          footer={
            <div className="space-y-2">
              <ThemeToggle className="w-full" />
              {meta ? (
                <div className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3">
                  {meta}
                </div>
              ) : null}
            </div>
          }
        >
          <AppNavigation label="Navegación principal" />
        </ShellRail>

        <div className="min-w-0 flex-1 space-y-4 p-2 md:p-4">
          <ShellHeader
            title={title}
            subtitle={subtitle}
            meta={meta}
            menuOpen={mobileOpen}
            drawerId={DRAWER_ID}
            triggerRef={triggerRef}
            onOpenMenu={() => setMobileOpen(true)}
            user={
              <span
                className="inline-flex h-9 w-9 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--color-accent)] text-xs font-bold text-[var(--color-accent-contrast)]"
                aria-label="Usuario autenticado"
              >
                DS
              </span>
            }
          />
          <div className="min-w-0">{children}</div>
        </div>
      </div>

      <ShellDrawer
        open={mobileOpen}
        onClose={() => setMobileOpen(false)}
        drawerId={DRAWER_ID}
        label="Menú principal"
        triggerRef={triggerRef}
      >
        <AppNavigation label="Navegación móvil principal" onNavigate={() => setMobileOpen(false)} />
        <div className="mt-4 space-y-2">
          <ThemeToggle className="w-full" />
          {meta ? (
            <div className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3">{meta}</div>
          ) : null}
        </div>
      </ShellDrawer>
    </main>
  );
}
