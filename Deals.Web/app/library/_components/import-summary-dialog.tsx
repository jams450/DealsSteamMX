"use client";

// Modal de cierre de las DOS importaciones (S1/S2/S4, odd/tasks/library-import-summary.md L2). Es uno solo
// porque el reporte del servidor también es uno solo: Playnite y Heroic escriben el mismo `user_library` y
// responden el mismo contrato. Dos modales habrían sido dos copias de las mismas reglas de conteo, y la
// primera que se desincronice miente.
//
// Lo que este componente NO hace:
//   · no inventa cambios: si el servidor no mandó el detalle aditivo (`modified`/`deleted`), dice que no está
//     disponible en vez de pintar 0, y nunca usa `updated` como si fuera `modified`,
//   · no inventa nombres: pinta títulos solo si el conteo es 1..50 y la lista llegó completa (`libraryImportSummary`),
//   · no borra nada: «Eliminados» es siempre 0 y el texto lo dice; no hay ninguna acción de borrado aquí.
//
// Accesibilidad: `role="dialog"` con nombre y descripción, foco contenido con `nextFocusIndex` (la misma
// utilidad del drawer y del alta de wishlist), Escape y botón de cierre, y el foco vuelve al botón que abrió el
// modal. El cuerpo hace scroll propio y los títulos largos rompen línea: 50 nombres no empujan el pie fuera de
// la pantalla en móvil.
import { useEffect, useRef, type RefObject } from "react";
import { CircleAlert, X } from "lucide-react";
import { nextFocusIndex } from "@/components/navigation/nav-config";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import { storeLabel } from "@/lib/contracts/stores";
import { libraryImportSummary, LIBRARY_IMPORT_DETAIL_LIMIT, type LibraryImportNamedItem, type LibraryImportReport } from "../_lib/library-contract";
import type { LibraryImportProvider } from "./library-import-panel";

type Props = {
  readonly provider: LibraryImportProvider;
  readonly report: LibraryImportReport;
  /** El servidor guardó, pero la lista no se pudo recargar. Es un aviso, nunca un fallo de importación. */
  readonly reloadFailed: boolean;
  /**
   * Encabezado del panel que abrió el modal (`HTMLElement` para no atar el diálogo a un nivel de título).
   * Recibe el foco al cerrar: es el único destino que sigue habilitado, porque el botón que disparó la
   * importación está `loading` durante la petición y vuelve a quedar deshabilitado al limpiarse el archivo.
   */
  readonly returnFocusRef: RefObject<HTMLElement | null>;
  readonly onClose: () => void;
};

/**
 * Aviso de "se guardó pero la lista no se recargó". Vive en este módulo, no en el cliente, porque lo usan dos
 * lugares con el mismo texto y el mismo tono: el cuerpo del modal (cuando sigue abierto) y la sección de
 * importación (cuando el usuario cerró el modal antes de que fallara la recarga, y reabrirlo sería mentir
 * sobre el resultado).
 */
export const IMPORT_RELOAD_WARNING =
  "La importación se guardó, pero la lista no se pudo recargar. Recarga la página para verla actualizada; no hace falta volver a subir el archivo.";

export function ImportReloadWarning() {
  return (
    <div
      role="alert"
      className="flex items-start gap-2 rounded-[var(--radius-md)] border border-[var(--color-warning)]/35 bg-[var(--color-warning)]/10 px-3 py-2.5 text-sm text-[var(--color-warning)]"
    >
      <CircleAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
      <div>{IMPORT_RELOAD_WARNING}</div>
    </div>
  );
}

const PROVIDER_LABELS: Readonly<Record<LibraryImportProvider, string>> = {
  playnite: "Playnite",
  heroic: "Heroic"
};

const PROVIDER_SOURCES: Readonly<Record<LibraryImportProvider, string>> = {
  playnite: "un export de Playnite",
  heroic: "un export de Heroic (solo Epic y GOG)"
};

// Mismo selector que el resto de los diálogos de la app; la decisión de a dónde mover el foco es la compartida.
const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function NamedList({ items }: { readonly items: readonly LibraryImportNamedItem[] }) {
  return (
    <ul className="max-h-40 space-y-1 overflow-y-auto rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-2">
      {items.map((item, index) => (
        <li key={`${item.platform}:${item.title}:${index}`} className="flex flex-wrap items-baseline gap-x-2 gap-y-1 text-xs">
          <span className="break-words font-semibold text-primary">{item.title}</span>
          <span className="tabler-badge tabler-badge-muted">{storeLabel(item.platform)}</span>
        </li>
      ))}
    </ul>
  );
}

function CountBadge({ label, count, tone }: { readonly label: string; readonly count: number; readonly tone: string }) {
  return (
    <span className={cn("tabler-badge", tone)}>
      {label} {count}
    </span>
  );
}

export function ImportSummaryDialog({ provider, report, reloadFailed, returnFocusRef, onClose }: Props) {
  const panelRef = useRef<HTMLElement>(null);
  // Último recurso: el elemento que tenía el foco al abrir, si era usable. Se prefiere el encabezado
  // explícito del panel porque ese elemento puede haberse deshabilitado (botón) o desaparecido.
  const capturedFocusRef = useRef<HTMLElement | null>(null);
  // El listener se monta una vez: `onClose` se lee por referencia para no volver a robar el foco si el padre
  // re-renderiza mientras el modal está abierto.
  const onCloseRef = useRef(onClose);
  // Mismo patrón que `onClose`: la referencia (el encabezado del panel) se lee al desmontar, así que cambiar el
  // prop no vuelve a montar el efecto ni roba el foco a mitad del modal.
  const focusReturnTarget = useRef(returnFocusRef);

  useEffect(() => {
    onCloseRef.current = onClose;
    focusReturnTarget.current = returnFocusRef;
  }, [onClose, returnFocusRef]);

  useEffect(() => {
    // El modal se abre cuando el servidor respondió. En ese instante el botón que lo disparó está `loading`
    // (deshabilitado), y al terminar bien el archivo pendiente se limpia y lo deja deshabilitado otra vez: el
    // foco del documento ya no está en un control utilizable. Por eso el destino es el encabezado del panel.
    const active = document.activeElement;
    capturedFocusRef.current = active instanceof HTMLElement && active !== document.body ? active : null;
    panelRef.current?.focus();

    function onKeyDown(event: KeyboardEvent) {
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
    }

    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      // El foco vuelve a la puerta que abrió el modal. Se usa la referencia explícita (el encabezado del
      // panel) porque el botón queda deshabilitado tanto al enviar como al limpiarse el archivo después del
      // éxito; `document.activeElement` ya no sirve.
      (focusReturnTarget.current.current ?? capturedFocusRef.current)?.focus();
    };
  }, []);

  const summary = libraryImportSummary(report);
  const addedCount = summary.imported;
  const updated = summary.updated;
  const unresolved = summary.unresolved;
  const unsupportedSource = summary.unsupportedSource;
  const deleted = summary.deleted;
  const modified = summary.modified;

  return (
    <div
      className="fixed inset-0 z-[70] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm"
      role="presentation"
      onClick={onClose}
    >
      <section
        ref={panelRef}
        tabIndex={-1}
        className="app-card max-h-[90dvh] w-full max-w-xl space-y-4 overflow-y-auto p-5 outline-none"
        role="dialog"
        aria-modal="true"
        aria-labelledby="import-summary-title"
        aria-describedby="import-summary-subtitle"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 id="import-summary-title" className="text-lg font-semibold text-primary">
              Resultado de la importación
            </h2>
            <p id="import-summary-subtitle" className="mt-0.5 break-words text-xs text-muted">
              <span className="tabler-badge tabler-badge-muted mr-1.5">{PROVIDER_LABELS[provider]}</span>
              El servidor guardó los cambios de {PROVIDER_SOURCES[provider]}.
            </p>
          </div>
          <Button type="button" variant="ghost" onClick={onClose} aria-label="Cerrar el resumen">
            <X className="h-3.5 w-3.5" aria-hidden="true" />
          </Button>
        </div>

        {reloadFailed ? <ImportReloadWarning /> : null}

        <div className="space-y-3" aria-live="polite">
          <div className="space-y-1">
            <p className="text-sm font-semibold text-primary">Cambios escritos</p>
            <div className="flex flex-wrap items-center gap-2">
              <CountBadge
                label="Agregados"
                count={addedCount}
                tone={addedCount > 0 ? "tabler-badge-success" : "tabler-badge-muted"}
              />
              {summary.detailed && modified !== null ? (
                <CountBadge
                  label="Modificados"
                  count={modified}
                  tone={modified > 0 ? "tabler-badge-info" : "tabler-badge-muted"}
                />
              ) : null}
              {summary.detailed && deleted !== null ? (
                <CountBadge label="Eliminados" count={deleted} tone="tabler-badge-muted" />
              ) : null}
            </div>
            {summary.detailed && modified !== null && deleted !== null ? (
              <p className="text-xs text-muted">
                «Modificados» cuenta solo las filas cuyo título, estado de instalación o vínculo con el catálogo
                cambió de verdad; «Actualizados» cuenta las filas existentes que se volvieron a escribir.
                «Eliminados» es {deleted}: importar nunca borra juegos de tu biblioteca.
              </p>
            ) : (
              <p className="text-xs text-muted">
                El servidor que respondió no incluye el detalle de modificados y eliminados, así que no se muestra:
                no se asume que sean 0. Los totales de abajo sí son válidos.
              </p>
            )}
          </div>

          <div className="space-y-1">
            <p className="text-sm font-semibold text-primary">Juegos agregados</p>
            {summary.addedNames.length > 0 ? (
              <NamedList items={summary.addedNames} />
            ) : addedCount === 0 ? (
              <p className="text-xs text-muted">
                No se agregó ningún juego: la biblioteca ya tenía todo lo que trae este archivo.
              </p>
            ) : (
              <p className="text-xs text-muted">
                {addedCount === 1 ? "1 juego nuevo" : `${addedCount} juegos nuevos`}. Esta respuesta no trae los
                títulos: se listan cuando son {LIBRARY_IMPORT_DETAIL_LIMIT} o menos y la lista llega completa.
              </p>
            )}
          </div>

          <div className="space-y-1">
            <p className="text-sm font-semibold text-primary">Juegos eliminados</p>
            {summary.deletedNames.length > 0 ? (
              <NamedList items={summary.deletedNames} />
            ) : deleted === 0 ? (
              <p className="text-xs text-muted">
                Ningún juego se eliminó. La importación solo agrega y actualiza: no borra ninguna fila de tu
                biblioteca.
              </p>
            ) : (
              <p className="text-xs text-muted">
                {deleted === null
                  ? "El detalle de eliminados no está disponible en esta respuesta."
                  : `${deleted} ${deleted === 1 ? "juego eliminado" : "juegos eliminados"}. Esta respuesta no trae los títulos.`}
              </p>
            )}
          </div>

          <div className="space-y-1">
            <p className="text-sm font-semibold text-primary">Resto del reporte</p>
            <div className="flex flex-wrap items-center gap-2">
              <CountBadge label="Actualizados" count={updated} tone="tabler-badge-info" />
              <CountBadge
                label="Sin resolver"
                count={unresolved}
                tone={unresolved > 0 ? "tabler-badge-warning" : "tabler-badge-muted"}
              />
              <CountBadge
                label="Fuente no soportada"
                count={unsupportedSource}
                tone={unsupportedSource > 0 ? "tabler-badge-danger" : "tabler-badge-muted"}
              />
            </div>
            {summary.byStore.length > 0 ? (
              <ul className="flex flex-wrap items-center gap-2">
                {summary.byStore.map((entry) => (
                  <li key={entry.store} className="tabler-badge tabler-badge-muted">
                    {storeLabel(entry.store)} {entry.count}
                  </li>
                ))}
              </ul>
            ) : null}
            <p className="text-xs text-muted">
              «Sin resolver» son entradas guardadas a las que todavía no se les asignó identidad en el catálogo del
              comparador. «Fuente no soportada» son filas con una tienda fuera del contrato del archivo. Reimportar
              no duplica ni borra nada.
            </p>
          </div>
        </div>

        <div className="flex flex-wrap justify-end gap-2">
          <Button type="button" variant="primary" onClick={onClose}>
            Cerrar
          </Button>
        </div>
      </section>
    </div>
  );
}
