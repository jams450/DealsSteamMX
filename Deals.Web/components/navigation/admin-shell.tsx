"use client";

import { ChevronDown, LogOut, Menu, X } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { Logo } from "@/components/brand/logo";
import { ThemeToggle } from "@/components/theme/theme-toggle";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { csrfFetch } from "@/lib/security/csrf-client";
import { cn } from "@/lib/ui/cn";
import { appNavItems, isRouteActive } from "./nav-config";

type AdminShellProps = { username: string; section: string; title: string; subtitle?: string; meta?: ReactNode; actions?: ReactNode; children: ReactNode };

export function AdminShell({ username, section, title, subtitle, meta, actions, children }: AdminShellProps) {
  const pathname = usePathname();
  const router = useRouter();
  const [loggingOut, setLoggingOut] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const [userOpen, setUserOpen] = useState(false);
  const closeRef = useRef<HTMLButtonElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const drawerRef = useRef<HTMLElement>(null);
  const userMenuRef = useRef<HTMLDivElement>(null);
  const userTriggerRef = useRef<HTMLButtonElement>(null);
  const previousBodyOverflowRef = useRef("");
  const hasOpenedMobileRef = useRef(false);
  const shortName = username.slice(0, 2).toUpperCase();

  useEffect(() => {
    if (!mobileOpen) {
      if (hasOpenedMobileRef.current) triggerRef.current?.focus();
      return;
    }

    hasOpenedMobileRef.current = true;
    previousBodyOverflowRef.current = document.body.style.overflow;
    closeRef.current?.focus();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setMobileOpen(false);
        return;
      }
      if (event.key !== "Tab" || !drawerRef.current) return;
      const focusable = drawerRef.current.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
      );
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
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
      document.body.style.overflow = previousBodyOverflowRef.current;
    };
  }, [mobileOpen]);

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
    try { await csrfFetch("/api/auth/logout", { method: "POST" }); router.replace("/login"); router.refresh(); }
    finally { setLoggingOut(false); }
  }

  function Navigation({ mobile = false }: { mobile?: boolean }) {
    return <nav className={cn("flex items-center gap-1 overflow-x-auto", mobile && "grid items-stretch gap-1 overflow-visible sm:grid-cols-2")} aria-label={mobile ? "Navegación móvil principal" : "Navegación principal"}>
      {appNavItems.map((item) => { const Icon = item.icon; const active = isRouteActive(pathname, item.href); return <Link key={item.href} href={item.href} onClick={() => mobile && setMobileOpen(false)} aria-current={active ? "page" : undefined} className={cn("flex h-10 shrink-0 items-center rounded-[var(--radius-sm)] border px-3 text-sm font-semibold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]", active ? "border-accent bg-[var(--color-accent-soft)] text-primary" : "border-transparent text-secondary hover:border-accent hover:bg-[var(--color-accent-soft)] hover:text-primary")}><Icon className="mr-2 h-4 w-4 shrink-0" aria-hidden="true" />{item.label}</Link>; })}
    </nav>;
  }

  const session = <section className="space-y-3 rounded-[var(--radius-md)] border border-accent bg-[var(--color-accent-soft)] p-3"><p className="text-[11px] font-semibold uppercase tracking-wide text-secondary">Sesión activa</p><div className="flex items-center gap-3"><span className="inline-flex h-9 w-9 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--color-accent)] text-xs font-bold text-[var(--color-accent-contrast)]">{shortName}</span><div className="min-w-0"><p className="truncate text-sm font-semibold text-primary">{username}</p><p className="text-[11px] text-muted">Administrador</p></div></div><ThemeToggle className="w-full" /><Button type="button" variant="ghost" loading={loggingOut} loadingText="Saliendo..." className="w-full" onClick={() => void onLogout()}><LogOut className="h-4 w-4" aria-hidden="true" />Cerrar sesión</Button></section>;

  return <main className="app-page"><div className="min-w-0 space-y-4 p-2 md:space-y-5">
    <header className="app-topbar flex min-h-16 flex-wrap items-center gap-3 px-3 py-2 sm:px-4"><Button ref={triggerRef} type="button" variant="ghost" className="h-10 w-10 shrink-0 p-0 lg:hidden" onClick={() => setMobileOpen(true)} aria-label="Abrir menú principal" aria-expanded={mobileOpen} aria-controls="mobile-navigation-drawer"><Menu className="h-5 w-5" aria-hidden="true" /></Button><Link href="/" className="flex shrink-0 items-center rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"><Logo /></Link><div className="hidden min-w-0 flex-1 lg:block"><Navigation /></div><div className="ml-auto flex items-center gap-2">{meta ? <div className="hidden sm:block">{meta}</div> : null}<ThemeToggle className="hidden sm:inline-flex" /><div ref={userMenuRef} className="relative hidden sm:block"><button ref={userTriggerRef} type="button" className="flex items-center gap-2 rounded-[var(--radius-sm)] p-1.5 text-left hover:bg-[var(--color-accent-soft)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]" onClick={() => setUserOpen((open) => !open)} aria-expanded={userOpen} aria-haspopup="menu"><span className="inline-flex h-8 w-8 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--color-accent)] text-xs font-bold text-[var(--color-accent-contrast)]">{shortName}</span><span className="max-w-28 truncate text-sm font-semibold text-primary">{username}</span><ChevronDown className="h-4 w-4 text-muted" aria-hidden="true" /></button>{userOpen ? <div className="absolute right-0 z-30 mt-2 w-48 rounded-[var(--radius-md)] border border-strong bg-[var(--color-surface-1)] p-2 shadow-[var(--shadow-md)]" role="menu"><p className="px-2 py-1 text-xs text-muted">Sesión activa</p><Button type="button" role="menuitem" variant="ghost" className="w-full justify-start" loading={loggingOut} onClick={() => void onLogout()}><LogOut className="h-4 w-4" aria-hidden="true" />Cerrar sesión</Button></div> : null}</div></div></header><div className="px-2 py-1"><p className="text-muted text-[11px] font-semibold uppercase tracking-[0.16em]">{section}</p><h1 className="mt-1 text-xl font-semibold tracking-tight text-primary md:text-2xl">{title}</h1>{subtitle ? <p className="mt-1 text-sm text-muted">{subtitle}</p> : null}</div>{actions ? <Card className="p-4">{actions}</Card> : null}<div>{children}</div></div>
    {mobileOpen ? <div className="fixed inset-0 z-50 lg:hidden" role="dialog" aria-modal="true" aria-label="Menú principal"><button type="button" className="absolute inset-0 bg-[var(--color-overlay)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]" onClick={() => setMobileOpen(false)} aria-label="Cerrar menú" /><aside ref={drawerRef} id="mobile-navigation-drawer" className="app-sidebar relative h-full w-[min(21rem,88vw)] overflow-y-auto p-3 shadow-[var(--shadow-md)]"><div className="mb-4 flex items-center justify-between border-b border-strong px-2 pb-4"><div><Logo markClassName="h-8 w-8" /><h2 className="mt-2 text-lg font-semibold text-primary">Control Center</h2></div><Button ref={closeRef} type="button" variant="ghost" className="h-10 w-10 p-0" onClick={() => setMobileOpen(false)} aria-label="Cerrar menú"><X className="h-5 w-5" aria-hidden="true" /></Button></div><Navigation mobile /><div className="mt-6">{session}</div></aside></div> : null}
  </main>;
}
