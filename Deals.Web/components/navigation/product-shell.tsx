"use client";

import { Menu, X } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { Logo } from "@/components/brand/logo";
import { ThemeToggle } from "@/components/theme/theme-toggle";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import { isRouteActive, productNavItems } from "./nav-config";

type ProductShellProps = {
  title: string;
  subtitle?: string;
  meta?: ReactNode;
  /** Sin tope de ancho: para páginas con tablas de muchas columnas. */
  wide?: boolean;
  children: ReactNode;
};

export function ProductShell({ title, subtitle, meta, wide = false, children }: ProductShellProps) {
  const pathname = usePathname();
  const [mobileOpen, setMobileOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const drawerRef = useRef<HTMLElement>(null);
  const openedRef = useRef(false);

  useEffect(() => {
    if (!mobileOpen) {
      if (openedRef.current) triggerRef.current?.focus();
      return;
    }

    openedRef.current = true;
    const previousOverflow = document.body.style.overflow;
    closeRef.current?.focus();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setMobileOpen(false);
        return;
      }
      if (event.key !== "Tab" || !drawerRef.current) return;
      const focusable = drawerRef.current.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])'
      );
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (!first || !last) return;
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };

    document.addEventListener("keydown", onKeyDown);
    document.body.style.overflow = "hidden";
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      document.body.style.overflow = previousOverflow;
    };
  }, [mobileOpen]);

  function Navigation({ mobile = false }: { mobile?: boolean }) {
    return (
      <nav
        aria-label={mobile ? "Navegación móvil principal" : "Navegación principal"}
        className={cn("flex items-center gap-1", mobile && "flex-col items-stretch")}
      >
        {productNavItems.map((item) => {
          const Icon = item.icon;
          const active = isRouteActive(pathname, item.href);
          return (
            <Link
              key={item.href}
              href={item.href}
              onClick={() => mobile && setMobileOpen(false)}
              aria-current={active ? "page" : undefined}
              className={cn(
                "flex h-10 items-center rounded-lg border px-3 text-sm font-semibold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                active
                  ? "border-accent bg-[var(--color-accent-soft)] text-primary"
                  : "border-transparent text-secondary hover:border-accent hover:bg-[var(--color-accent-soft)] hover:text-primary"
              )}
            >
              <Icon className="mr-2 h-4 w-4 shrink-0" aria-hidden="true" />
              {item.label}
            </Link>
          );
        })}
      </nav>
    );
  }

  return (
    <main className="app-page">
      <div className={cn("mx-auto space-y-4 p-2 md:p-4", wide ? "w-full max-w-none" : "max-w-7xl")}>
        <header className="app-topbar flex min-h-16 items-center gap-3 px-3 py-2 md:px-4">
          <Button
            ref={triggerRef}
            type="button"
            variant="ghost"
            className="h-10 w-10 p-0 md:hidden"
            onClick={() => setMobileOpen(true)}
            aria-label="Abrir menú principal"
            aria-expanded={mobileOpen}
            aria-controls="product-navigation-drawer"
          >
            <Menu className="h-5 w-5" aria-hidden="true" />
          </Button>
          <Link
            href="/"
            className="flex shrink-0 items-center rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
          >
            <Logo />
          </Link>
          <div className="hidden md:block">
            <Navigation />
          </div>
          <div className="ml-auto flex items-center gap-2">
            {meta ? <div className="hidden sm:block">{meta}</div> : null}
            <ThemeToggle className="hidden sm:inline-flex" />
            <span
              className="inline-flex h-9 w-9 items-center justify-center rounded-lg bg-[var(--color-accent)] text-xs font-bold text-[var(--color-accent-contrast)]"
              aria-label="Usuario autenticado"
            >
              DS
            </span>
          </div>
        </header>
        <header className="px-2 py-1">
          <h1 className="text-xl font-semibold tracking-tight text-primary md:text-2xl">{title}</h1>
          {subtitle ? <p className="mt-1 text-sm text-muted">{subtitle}</p> : null}
        </header>
        <div>{children}</div>
      </div>
      {mobileOpen ? (
        <div className="fixed inset-0 z-50 md:hidden" role="dialog" aria-modal="true" aria-label="Menú principal">
          <button
            type="button"
            className="absolute inset-0 bg-[var(--color-overlay)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
            onClick={() => setMobileOpen(false)}
            aria-label="Cerrar menú"
          />
          <aside ref={drawerRef} id="product-navigation-drawer" className="app-sidebar relative h-full w-[min(21rem,88vw)] overflow-y-auto p-3 shadow-[var(--shadow-md)]">
            <div className="mb-4 flex items-center justify-between border-b border-strong px-2 pb-4">
              <Logo markClassName="h-7 w-7" />
              <Button ref={closeRef} type="button" variant="ghost" className="h-10 w-10 p-0" onClick={() => setMobileOpen(false)} aria-label="Cerrar menú">
                <X className="h-5 w-5" aria-hidden="true" />
              </Button>
            </div>
            <Navigation mobile />
            <ThemeToggle className="mt-6 w-full" />
          </aside>
        </div>
      ) : null}
    </main>
  );
}
