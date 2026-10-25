"use client";

// Una de las dos puertas de importación de la biblioteca, pintada con el mismo molde (S1/S4,
// odd/tasks/library-import-summary.md): mismo encabezado, mismo aviso de compatibilidad, misma fila de
// archivo, mismo estado y mismo botón. Solo cambia el contenido, porque solo cambia de dónde sale el JSON:
//   · Playnite: todas las tiendas que el usuario tenga ahí (Steam, Epic, GOG, Xbox, Amazon y el resto),
//   · Heroic: solo Epic y GOG, desde la caché local de Heroic, sin conectarse a ninguna cuenta.
//
// El panel no importa nada: recibe el archivo ya validado por el padre y avisa. Una sola importación puede
// estar en curso a la vez (`processing`), así que el panel de la otra puerta también se deshabilita: dos
// peticiones simultáneas abrirían dos resultados y dejarían la lista a medio refrescar.
import type { ChangeEvent, ReactNode, RefObject } from "react";
import { FileJson, Upload } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";

export type LibraryImportProvider = "playnite" | "heroic";

type Panel = {
  readonly kicker: string;
  readonly title: string;
  readonly description: string;
  readonly fileLabel: string;
  readonly hint: ReactNode;
  readonly cta: string;
};

// Copy fija, así que vive fuera del componente y no se reconstruye en cada render.
const PANELS: Readonly<Record<LibraryImportProvider, Panel>> = {
  playnite: {
    kicker: "Playnite",
    title: "Todas tus tiendas en un archivo",
    description:
      "El export de Playnite trae todas las tiendas que tengas ahí —Steam, Epic, GOG, Xbox, Amazon y el resto—, con la fecha de alta y si está instalado. Es la puerta completa.",
    fileLabel: "Archivo JSON del export de Playnite",
    hint: "Un arreglo JSON en la raíz, máximo 10 MiB. Se revisa en tu navegador antes de enviarlo.",
    cta: "Importar Playnite"
  },
  heroic: {
    kicker: "Heroic",
    title: "Solo Epic y GOG",
    description:
      "Para quien no usa Playnite: el script de Heroic lee su caché local y produce un sobre versionado. No se conecta a tu cuenta ni pide usuario. Steam sigue entrando con tu perfil público y el resto, con Playnite.",
    fileLabel: "Archivo JSON del export de Heroic",
    hint: (
      <>
        Sobre con <code>dealext-library</code>, <code>version 1</code> y <code>source &quot;heroic&quot;</code>,
        máximo 10 MiB. Se revisa en tu navegador antes de enviarlo.
      </>
    ),
    cta: "Importar Heroic"
  }
};

type Props = {
  readonly provider: LibraryImportProvider;
  readonly inputId: string;
  readonly inputRef: RefObject<HTMLInputElement | null>;
  /**
   * Encabezado del panel: destino del foco cuando el modal de resultado se cierra. Es el único control que
   * SIEMPRE puede recibir foco en esta puerta: el botón queda deshabilitado mientras se envía (`loading`) y
   * otra vez al terminar bien (el archivo pendiente se limpia, así que `ready` vuelve a false). Apuntar al
   * botón dejaba el foco en `<body>`; el encabezado, con `tabIndex={-1}`, nunca está deshabilitado y nombra la
   * puerta que el usuario acaba de usar.
   */
  readonly headingRef: RefObject<HTMLHeadingElement | null>;
  readonly fileError: string | null;
  /** Fallo real de la petición. El fallo de recarga de la lista no llega aquí: lo cuenta el modal. */
  readonly requestError: string | null;
  /** Hay una importación en curso (de esta puerta o de la otra). */
  readonly processing: boolean;
  /** La importación en curso es la de este panel. */
  readonly importing: boolean;
  /** Este archivo ya se validó y se puede enviar. */
  readonly ready: boolean;
  /** Línea de estado en voz alta: "archivo listo", "enviando", cuántos juegos trae. */
  readonly status: string;
  readonly onFileChange: (event: ChangeEvent<HTMLInputElement>) => void;
  readonly onImport: () => void;
};

export function LibraryImportPanel({
  provider,
  inputId,
  inputRef,
  headingRef,
  fileError,
  requestError,
  processing,
  importing,
  ready,
  status,
  onFileChange,
  onImport
}: Props) {
  const panel = PANELS[provider];
  const hintId = `${inputId}-hint`;

  return (
    <section
      className="flex min-w-0 flex-col gap-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4"
      aria-labelledby={`${inputId}-title`}
    >
      <div className="space-y-1">
        <span className="block text-xs font-semibold uppercase tracking-widest text-muted">{panel.kicker}</span>
        <h3
          id={`${inputId}-title`}
          ref={headingRef}
          tabIndex={-1}
          className="rounded-[var(--radius-sm)] text-sm font-semibold text-primary outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)] focus-visible:ring-offset-2 focus-visible:ring-offset-[var(--color-surface-2)]"
        >
          {panel.title}
        </h3>
        <p className="text-xs text-muted">{panel.description}</p>
      </div>

      <div className="flex items-center gap-2">
        <FileJson className="h-4 w-4 shrink-0 text-muted" aria-hidden="true" />
        <label htmlFor={inputId} className="text-sm font-semibold text-primary">
          {panel.fileLabel}
        </label>
      </div>
      <input
        id={inputId}
        ref={inputRef}
        type="file"
        accept=".json,application/json"
        onChange={onFileChange}
        disabled={processing}
        aria-describedby={hintId}
        className="input-semantic block min-h-10 w-full min-w-0 cursor-pointer px-3 py-1.5 text-sm file:mr-3 file:cursor-pointer file:rounded-[var(--radius-sm)] file:border-0 file:bg-[var(--color-surface-3)] file:px-3 file:py-1 file:text-sm file:font-semibold file:text-primary disabled:cursor-not-allowed disabled:opacity-60"
      />
      <p id={hintId} className="text-xs text-muted">
        {panel.hint}
      </p>

      {/* `mt-auto` empareja los dos botones al pie: los textos miden distinto, las acciones no. */}
      <div className="mt-auto flex flex-wrap items-center gap-3">
        <Button
          type="button"
          variant="secondary"
          loading={importing}
          loadingText="Importando..."
          disabled={processing || !ready}
          onClick={onImport}
        >
          <Upload className="h-4 w-4" aria-hidden="true" />
          {panel.cta}
        </Button>
        <span className="text-xs text-muted" aria-live="polite">
          {status}
        </span>
      </div>

      {fileError ? <Alert variant="danger">{fileError}</Alert> : null}
      {requestError ? <Alert variant="danger">{requestError}</Alert> : null}
    </section>
  );
}
