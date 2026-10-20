"use client";

import { ChevronDown, LogOut } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { ThemeToggle } from "@/components/theme/theme-toggle";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { csrfFetch } from "@/lib/security/csrf-client";
import { AppNavigation } from "./app-navigation";
import { ShellDrawer, ShellHeader, ShellRail } from "./shell-chrome";

type AdminShellProps = {
  username: string;
  section: string;
  title: string;
  subtitle?: string;
  meta?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
};

const DRAWER_ID = "mobile-navigation-drawer";

/**
 * Superficie de administración. Mismo riel y mismo drawer que `ProductShell` (chrome
 * compartido), pero conserva lo suyo: kicker de sección, `actions` en tarjeta, menú de usuario
 * con cierre de sesión y el bloque de sesión en el riel y en el drawer.
 */
export function AdminShell({ username, section, title, subtitle, meta, actions, children }: AdminShellProps) {
  const router = useRouter();
  const [loggingOut, setLoggingOut] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const [userOpen, setUserOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const userMenuRef = useRef<HTMLDivElement>(null);
  const userTriggerRef = useRef<HTMLButtonElement>(null);
  const shortName = username.slice(0, 2).toUpperCase();

  useEffect(() => {
    if (!userOpen) return;
    const onDocumentClick = (event: MouseEvent) => {
      if (!userMenuRef.current?.contains(event.target as Node)) setUserOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setUserOpen(false);
        userTriggerRef.current?.focus();
      }
    };
    document.addEventListener("click", onDocumentClick);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("click", onDocumentClick);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [userOpen]);

  async function onLogout() {
    setLoggingOut(true);
    try {
      await csrfFetch("/api/auth/logout", { method: "POST" });
      router.replace("/login");
      router.refresh();
    } finally {
      setLoggingOut(false);
    }
  }

  const session = (
    <div className="space-y-2">
      <div className="flex items-center gap-2 px-1">
        <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--color-accent)] text-xs font-bold text-[var(--color-accent-contrast)]">
          {shortName}
        </span>
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold text-primary">{username}</p>
          <p className="text-[11px] text-muted">Administrador</p>
        </div>
      </div>
      <ThemeToggle className="w-full" />
      <Button
        type="button"
        variant="ghost"
        loading={loggingOut}
        loadingText="Saliendo..."
        className="w-full justify-start"
        onClick={() => void onLogout()}
      >
        <LogOut className="h-4 w-4" aria-hidden="true" />
        Cerrar sesión
      </Button>
    </div>
  );

  return (
    <main className="app-page">
      <div className="flex w-full items-start">
        <ShellRail
          footer={
            <div className="space-y-2">
              {session}
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

        <div className="min-w-0 flex-1 space-y-4 p-2 md:space-y-5 md:p-4">
          <ShellHeader
            title={title}
            subtitle={subtitle}
            section={section}
            meta={meta}
            menuOpen={mobileOpen}
            drawerId={DRAWER_ID}
            triggerRef={triggerRef}
            onOpenMenu={() => setMobileOpen(true)}
            user={
              <div ref={userMenuRef} className="relative hidden sm:block">
                <button
                  ref={userTriggerRef}
                  type="button"
                  className="flex items-center gap-2 rounded-[var(--radius-sm)] p-1.5 text-left hover:bg-[var(--color-accent-soft)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                  onClick={() => setUserOpen((open) => !open)}
                  aria-expanded={userOpen}
                  aria-haspopup="menu"
                >
                  <span className="inline-flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--color-accent)] text-xs font-bold text-[var(--color-accent-contrast)]">
                    {shortName}
                  </span>
                  <span className="max-w-28 truncate text-sm font-semibold text-primary">{username}</span>
                  <ChevronDown className="h-4 w-4 text-muted" aria-hidden="true" />
                </button>
                {userOpen ? (
                  <div
                    className="absolute right-0 z-30 mt-2 w-48 rounded-[var(--radius-md)] border border-strong bg-[var(--color-surface-1)] p-2 shadow-[var(--shadow-md)]"
                    role="menu"
                  >
                    <p className="px-2 py-1 text-xs text-muted">Sesión activa</p>
                    <Button
                      type="button"
                      role="menuitem"
                      variant="ghost"
                      className="w-full justify-start"
                      loading={loggingOut}
                      onClick={() => void onLogout()}
                    >
                      <LogOut className="h-4 w-4" aria-hidden="true" />
                      Cerrar sesión
                    </Button>
                  </div>
                ) : null}
              </div>
            }
          />
          {actions ? <Card className="p-4">{actions}</Card> : null}
          <div className="min-w-0">{children}</div>
        </div>
      </div>

      <ShellDrawer
        open={mobileOpen}
        onClose={() => setMobileOpen(false)}
        drawerId={DRAWER_ID}
        label="Menú principal"
        triggerRef={triggerRef}
        heading={<h2 className="mt-1 text-lg font-semibold text-primary">Control Center</h2>}
      >
        <AppNavigation label="Navegación móvil principal" onNavigate={() => setMobileOpen(false)} />
        <div className="mt-4 space-y-2">
          {session}
          {meta ? (
            <div className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3">{meta}</div>
          ) : null}
        </div>
      </ShellDrawer>
    </main>
  );
}
