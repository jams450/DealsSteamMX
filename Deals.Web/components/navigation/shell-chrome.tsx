"use client";

import { Menu, X } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef, type ReactNode, type RefObject } from "react";
import { Logo } from "@/components/brand/logo";
import { ThemeToggle } from "@/components/theme/theme-toggle";
import { Button } from "@/components/ui/button";
import { nextFocusIndex } from "./nav-config";

/*
 * Chrome compartido por `ProductShell` y `AdminShell`.
 *
 * Las dos superficies movieron su navegación a un riel izquierdo, así que el riel, el drawer
 * móvil y el header viven una sola vez: si cada shell los dibujara por su cuenta, la trampa de
 * foco, el `Escape` y el retorno de foco ya estarían desincronizados en el primer arreglo.
 * Sin dependencias nuevas: tokens existentes (`.app-sidebar`, `.app-rail`, `.app-topbar`) y los
 * primitivos `Button`/`Logo`/`ThemeToggle`.
 */

const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** El mismo corte que `lg` en Tailwind: a partir de ahí el riel sustituye al drawer. */
const DESKTOP_QUERY = "(min-width: 64rem)";

type ShellRailProps = {
  children: ReactNode;
  /** Bloque fijo al pie del riel (tema, sesión). Nunca se desplaza con la lista de rutas. */
  footer?: ReactNode;
};

/**
 * Riel izquierdo de escritorio: `sticky`, altura de viewport y scroll propio, así que una lista
 * de rutas larga no arrastra el contenido ni obliga a desplazar la página entera para llegar al
 * pie. Debajo de `lg` no se monta visualmente (`.app-rail` es `display:none`); ahí manda el drawer.
 */
export function ShellRail({ children, footer }: ShellRailProps) {
  return (
    <aside className="app-rail app-sidebar">
      <Link
        href="/"
        className="mb-3 flex shrink-0 items-center rounded-[var(--radius-sm)] px-2 py-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
      >
        <Logo />
      </Link>
      <div className="app-rail-scroll">{children}</div>
      {footer ? <div className="mt-3 shrink-0 border-t border-strong pt-3">{footer}</div> : null}
    </aside>
  );
}

type ShellDrawerProps = {
  open: boolean;
  onClose: () => void;
  /** Id del panel: el disparador del header lo referencia con `aria-controls`. */
  drawerId: string;
  /** Nombre accesible del `dialog`. */
  label: string;
  /** Línea extra bajo la marca (p. ej. el nombre de la superficie de admin). */
  heading?: ReactNode;
  triggerRef: RefObject<HTMLButtonElement | null>;
  children: ReactNode;
};

/**
 * Drawer móvil. Mantiene el contrato accesible completo: `role="dialog"`, `aria-modal`,
 * trampa de foco, `Escape`, retorno del foco al disparador, backdrop que cierra, scroll del
 * body bloqueado y cierre automático cuando cambia la ruta (o cuando el viewport salta al
 * ancho de escritorio con el drawer abierto, que dejaría el body bloqueado sin drawer visible).
 */
export function ShellDrawer({ open, onClose, drawerId, label, heading, triggerRef, children }: ShellDrawerProps) {
  const pathname = usePathname();
  const panelRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const openedRef = useRef(false);
  const openedPathRef = useRef<string | null>(null);
  const onCloseRef = useRef(onClose);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  // Cerrar al cambiar de ruta. Un clic en un enlace del drawer ya lo cierra; esto cubre el
  // caso que ese clic no ve: atrás/adelante del navegador con el drawer abierto.
  useEffect(() => {
    if (!open) {
      openedPathRef.current = null;
      return;
    }
    if (openedPathRef.current === null) {
      openedPathRef.current = pathname;
      return;
    }
    if (openedPathRef.current !== pathname) {
      openedPathRef.current = null;
      onCloseRef.current();
    }
  }, [open, pathname]);

  useEffect(() => {
    if (!open) {
      if (openedRef.current) {
        openedRef.current = false;
        triggerRef.current?.focus();
      }
      return;
    }

    openedRef.current = true;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    closeRef.current?.focus();

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        onCloseRef.current();
        return;
      }
      if (event.key !== "Tab" || !panelRef.current) return;
      const focusable = Array.from(panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
      const next = nextFocusIndex(
        focusable.indexOf(document.activeElement as HTMLElement),
        focusable.length,
        event.shiftKey
      );
      if (next === null) return;
      event.preventDefault();
      focusable[next]?.focus();
    };

    document.addEventListener("keydown", onKeyDown);

    const desktopQuery = window.matchMedia(DESKTOP_QUERY);
    const onDesktopChange = (event: MediaQueryListEvent) => {
      if (event.matches) onCloseRef.current();
    };
    desktopQuery.addEventListener("change", onDesktopChange);

    return () => {
      document.removeEventListener("keydown", onKeyDown);
      desktopQuery.removeEventListener("change", onDesktopChange);
      document.body.style.overflow = previousOverflow;
    };
  }, [open, triggerRef]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 lg:hidden" role="dialog" aria-modal="true" aria-label={label} id={drawerId}>
      <button
        type="button"
        className="absolute inset-0 h-full w-full cursor-default bg-[var(--color-overlay)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
        onClick={onClose}
        aria-label="Cerrar menú"
        tabIndex={-1}
      />
      <div
        ref={panelRef}
        className="app-sidebar relative flex h-full w-[min(21rem,88vw)] flex-col overflow-hidden shadow-[var(--shadow-md)]"
      >
        <div className="flex shrink-0 items-start justify-between gap-2 border-b border-strong p-3">
          <div className="min-w-0">
            <Logo markClassName="h-7 w-7" />
            {heading}
          </div>
          <Button
            ref={closeRef}
            type="button"
            variant="ghost"
            className="h-10 w-10 shrink-0 p-0"
            onClick={onClose}
            aria-label="Cerrar menú"
          >
            <X className="h-5 w-5" aria-hidden="true" />
          </Button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain p-3">{children}</div>
      </div>
    </div>
  );
}

type ShellHeaderProps = {
  title: string;
  subtitle?: string;
  /** Kicker de superficie (solo admin). */
  section?: string;
  meta?: ReactNode;
  /** Control de sesión: insignia estática en producto, menú con cierre de sesión en admin. */
  user: ReactNode;
  menuOpen: boolean;
  drawerId: string;
  triggerRef: RefObject<HTMLButtonElement | null>;
  onOpenMenu: () => void;
};

/**
 * Header delgado: disparador del drawer (solo móvil), título/subtítulo y los controles. La
 * navegación ya no vive aquí, que era justo lo que se amontonaba en pantallas pequeñas.
 */
export function ShellHeader({
  title,
  subtitle,
  section,
  meta,
  user,
  menuOpen,
  drawerId,
  triggerRef,
  onOpenMenu
}: ShellHeaderProps) {
  return (
    <header className="app-topbar flex min-h-16 min-w-0 items-center gap-2 px-2 py-2 sm:gap-3 sm:px-3 md:px-4">
      <Button
        ref={triggerRef}
        type="button"
        variant="ghost"
        className="h-10 w-10 shrink-0 p-0 lg:hidden"
        onClick={onOpenMenu}
        aria-label="Abrir menú principal"
        aria-expanded={menuOpen}
        aria-controls={drawerId}
      >
        <Menu className="h-5 w-5" aria-hidden="true" />
      </Button>
      <Link
        href="/"
        className="flex shrink-0 items-center rounded-[var(--radius-sm)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)] lg:hidden"
      >
        <Logo />
      </Link>
      <div className="min-w-0 flex-1">
        {section ? (
          <p className="text-[11px] font-semibold uppercase tracking-widest text-muted">{section}</p>
        ) : null}
        <h1 className="truncate text-base font-semibold tracking-tight text-primary md:text-xl">{title}</h1>
        {subtitle ? <p className="line-clamp-2 text-xs leading-snug text-muted md:text-sm">{subtitle}</p> : null}
      </div>
      <div className="flex shrink-0 items-center gap-1.5 sm:gap-2">
        {meta ? <div className="hidden sm:block">{meta}</div> : null}
        <ThemeToggle className="hidden sm:inline-flex" />
        {user}
      </div>
    </header>
  );
}
