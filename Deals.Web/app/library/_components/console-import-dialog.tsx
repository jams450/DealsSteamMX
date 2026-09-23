"use client";

// Importación masiva de la colección de consola desde el export de Playnite (docs/PLAN_CONSOLE.md §7).
// Tres pasos en un solo panel:
//   1. Archivo: se lee el JSON en el navegador y se separa lo que este contrato acepta de lo que no
//      (filas de tienda, ids repetidos, títulos fuera de rango). Nada se envía todavía.
//   2. Revisar: el preview del servidor trae candidatos y plataformas sugeridas; cada fila resuelve a
//      mano su plataforma y elige candidato existente o juego nuevo.
//   3. Resultado: el commit reporta creados / enlazados / ya presentes, o un rechazo sin escrituras si
//      hay un conflicto de identidad. Tras un commit aplicado, la biblioteca se recarga.
//
// El diálogo es un envoltorio de estado: el parseo y las decisiones puras viven en
// `lib/contracts/console-import.ts` (cubierto por `node --test`) y el paso de revisión en
// `console-import-review.tsx`.
import { useEffect, useMemo, useRef, useState, type ChangeEvent } from "react";
import { Gamepad2, Upload, X } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/ui/cn";
import { storeLabel } from "@/lib/contracts/stores";
import {
  CONSOLE_IMPORT_MAX_BYTES,
  CONSOLE_IMPORT_MAX_ENTRIES,
  buildConsoleImportDecision,
  initialConsoleImportDraft,
  isConsoleImportDraftReady,
  parseConsoleImportFile,
  type ConsoleImportCommitResult,
  type ConsoleImportDecisionDraft,
  type ConsoleImportFile,
  type ConsoleImportOutcome,
  type ConsoleImportPreview
} from "@/lib/contracts/console-import";
import { commitConsoleImport, previewConsoleImport } from "../_lib/console-import-api";
import { ConsoleImportFileSummary } from "./console-import-file-summary";
import { ConsoleImportReview } from "./console-import-review";

type Props = {
  onClose: () => void;
  /** Se dispara tras un commit aplicado: la biblioteca se recarga fuera de aquí. */
  onImported: () => void;
};

type Step = "load" | "review" | "result";

const STEP_LABELS: readonly { readonly value: Step; readonly label: string }[] = [
  { value: "load", label: "Archivo" },
  { value: "review", label: "Revisar" },
  { value: "result", label: "Resultado" }
];

const OUTCOME_LABELS: Readonly<Record<ConsoleImportOutcome, string>> = {
  created: "Juego nuevo",
  attached: "Enlazado a un juego existente",
  already_present: "Ya estaba en tu biblioteca"
};

const OUTCOME_BADGES: Readonly<Record<ConsoleImportOutcome, string>> = {
  created: "tabler-badge tabler-badge-success",
  attached: "tabler-badge tabler-badge-info",
  already_present: "tabler-badge tabler-badge-muted"
};

// El límite se comprueba dos veces: `file.size` para el aviso inmediato y los bytes ya leídos porque el
// límite real lo aplica el BFF. El archivo nunca se guarda: solo se reenvía el JSON a la API.
function readConsoleFile(file: File): Promise<unknown> {
  if (file.size > CONSOLE_IMPORT_MAX_BYTES) {
    return Promise.reject(new Error("El archivo supera el límite de 10 MiB."));
  }

  return file.text().then((text) => {
    if (new TextEncoder().encode(text).length > CONSOLE_IMPORT_MAX_BYTES) {
      throw new Error("El archivo supera el límite de 10 MiB.");
    }

    try {
      return JSON.parse(text) as unknown;
    } catch {
      throw new Error("El archivo no es JSON válido.");
    }
  });
}

function StepMarker({ current }: { readonly current: Step }) {
  const currentIndex = STEP_LABELS.findIndex((entry) => entry.value === current);

  return (
    <ol className="flex flex-wrap items-center gap-2" aria-label="Pasos de la importación">
      {STEP_LABELS.map((entry, position) => {
        const state = position < currentIndex ? "done" : position === currentIndex ? "current" : "next";
        return (
          <li key={entry.value} className="flex items-center gap-2">
            <span
              className={cn(
                "inline-flex h-6 items-center gap-1.5 rounded-full px-2 text-[11px] font-semibold uppercase tracking-wide",
                state === "current" && "bg-[var(--color-accent)] text-[var(--color-accent-contrast)]",
                state === "done" && "bg-[var(--color-accent-soft)] text-accent",
                state === "next" && "text-muted"
              )}
              aria-current={state === "current" ? "step" : undefined}
            >
              <span className="tabular-nums">{position + 1}</span>
              {entry.label}
            </span>
            {position < STEP_LABELS.length - 1 ? <span className="text-muted" aria-hidden="true">·</span> : null}
          </li>
        );
      })}
    </ol>
  );
}

export function ConsoleImportDialog({ onClose, onImported }: Props) {
  const [step, setStep] = useState<Step>("load");
  const [fileName, setFileName] = useState<string | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [file, setFile] = useState<ConsoleImportFile | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [preview, setPreview] = useState<ConsoleImportPreview | null>(null);
  const [drafts, setDrafts] = useState<ReadonlyMap<string, ConsoleImportDecisionDraft>>(new Map());
  const [committing, setCommitting] = useState(false);
  const [commitError, setCommitError] = useState<string | null>(null);
  const [result, setResult] = useState<ConsoleImportCommitResult | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const panelRef = useRef<HTMLElement>(null);

  const busy = previewing || committing;

  useEffect(() => {
    panelRef.current?.focus();
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape" && !busy) onClose();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [busy, onClose]);

  const sourceByEntryId = useMemo(
    () => new Map((file?.entries ?? []).map((entry) => [entry.entryId, entry] as const)),
    [file]
  );

  const previewEntries = useMemo(() => preview?.entries ?? [], [preview]);
  const titleByEntryId = useMemo(
    () => new Map(previewEntries.map((entry) => [entry.entryId, entry.name] as const)),
    [previewEntries]
  );

  const readyEntries = useMemo(
    () => previewEntries.filter((entry) => {
      const draft = drafts.get(entry.entryId);
      return draft !== undefined && isConsoleImportDraftReady(draft);
    }),
    [drafts, previewEntries]
  );
  const unresolvedCount = previewEntries.length - readyEntries.length;

  function reset() {
    setStep("load");
    setFileName(null);
    setFileError(null);
    setFile(null);
    setPreviewError(null);
    setPreview(null);
    setDrafts(new Map());
    setCommitError(null);
    setResult(null);
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  function onFileChange(event: ChangeEvent<HTMLInputElement>) {
    const input = event.currentTarget;
    const selected = input.files?.[0] ?? null;

    setFileError(null);
    setPreviewError(null);
    setCommitError(null);
    setPreview(null);
    setDrafts(new Map());
    setResult(null);
    setFileName(selected?.name ?? null);
    setFile(null);

    if (selected === null) return;

    void readConsoleFile(selected)
      .then((raw) => {
        const parsed = parseConsoleImportFile(raw);
        if (parsed === null) {
          setFileError("El JSON debe tener un arreglo en la raíz, como el export de Playnite.");
          input.value = "";
          setFileName(null);
          return;
        }
        setFile(parsed);
      })
      .catch((cause: unknown) => {
        setFileError(cause instanceof Error ? cause.message : "No se pudo leer el archivo.");
        input.value = "";
        setFileName(null);
      });
  }

  async function runPreview() {
    if (file === null || file.entries.length === 0 || previewing) return;

    // Defensa: el botón ya está deshabilitado, pero un archivo por encima del tope no se previsualiza
    // para no dejar resolver miles de filas que el commit atómico rechazaría al final.
    if (file.exceedsEntryLimit) {
      setPreviewError(
        `Este archivo trae ${file.entries.length} entradas de consola y el tope por importación es ${CONSOLE_IMPORT_MAX_ENTRIES}. ` +
          "Divide el export en partes y repite la importación con cada una."
      );
      return;
    }

    setPreviewing(true);
    setPreviewError(null);
    try {
      const nextPreview = await previewConsoleImport(file.entries);
      const nextDrafts = new Map<string, ConsoleImportDecisionDraft>();
      for (const entry of nextPreview.entries) {
        nextDrafts.set(entry.entryId, initialConsoleImportDraft(entry));
      }
      setPreview(nextPreview);
      setDrafts(nextDrafts);
      setStep("review");
    } catch (cause) {
      setPreviewError(cause instanceof Error ? cause.message : "No se pudo previsualizar la importación.");
    } finally {
      setPreviewing(false);
    }
  }

  function onChangePlatform(entryId: string, platform: string) {
    setDrafts((current) => {
      const draft = current.get(entryId);
      if (draft === undefined) return current;
      const next = new Map(current);
      next.set(entryId, { ...draft, platform });
      return next;
    });
  }

  function onChangeAttach(entryId: string, attachGameId: number | null) {
    setDrafts((current) => {
      const draft = current.get(entryId);
      if (draft === undefined) return current;
      const next = new Map(current);
      next.set(entryId, { ...draft, attachGameId });
      return next;
    });
  }

  async function runCommit() {
    if (committing || readyEntries.length === 0) return;

    if (readyEntries.length > CONSOLE_IMPORT_MAX_ENTRIES) {
      setCommitError(
        `El servidor acepta hasta ${CONSOLE_IMPORT_MAX_ENTRIES} entradas por importación y hay ${readyEntries.length} listas. ` +
          "Vuelve al archivo y divide el export en partes más pequeñas."
      );
      return;
    }

    const decisions = [];
    for (const entry of readyEntries) {
      const draft = drafts.get(entry.entryId);
      const source = sourceByEntryId.get(entry.entryId);
      if (draft === undefined || source === undefined) continue;
      const decision = buildConsoleImportDecision(draft, source);
      if (decision !== null) decisions.push(decision);
    }

    if (decisions.length === 0) {
      setCommitError("Ninguna fila tiene una plataforma válida todavía.");
      return;
    }

    setCommitting(true);
    setCommitError(null);
    setResult(null);
    try {
      const report = await commitConsoleImport(decisions);
      setResult(report);
      setStep("result");
      if (report.applied) onImported();
    } catch (cause) {
      setCommitError(cause instanceof Error ? cause.message : "No se pudo importar la biblioteca de consolas.");
    } finally {
      setCommitting(false);
    }
  }

  return (
    <div
      className="fixed inset-0 z-[70] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm"
      role="presentation"
      onClick={busy ? undefined : onClose}
    >
      <section
        ref={panelRef}
        tabIndex={-1}
        className="app-card flex max-h-[92dvh] w-full max-w-3xl flex-col gap-4 overflow-hidden p-5 outline-none"
        role="dialog"
        aria-modal="true"
        aria-labelledby="console-import-title"
        onClick={(event) => event.stopPropagation()}
      >
        <header className="flex items-start justify-between gap-3">
          <div className="min-w-0 space-y-1.5">
            <h2 id="console-import-title" className="flex items-center gap-2 text-lg font-semibold text-primary">
              <Gamepad2 className="h-4 w-4" aria-hidden="true" />
              Importar consolas desde Playnite
            </h2>
            <p className="text-xs text-muted">
              Sube el export completo: las filas de tienda se descartan aquí y las de consola se resuelven una por una.
              Nada se escribe hasta que confirmes.
            </p>
            <StepMarker current={step} />
          </div>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy} aria-label="Cerrar">
            <X className="h-3.5 w-3.5" aria-hidden="true" />
          </Button>
        </header>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1">
          {fileError ? <Alert variant="danger">{fileError}</Alert> : null}
          {previewError ? <Alert variant="danger">{previewError}</Alert> : null}
          {commitError ? <Alert variant="danger">{commitError}</Alert> : null}

          {step === "load" ? (
            <div className="space-y-3">
              <div className="space-y-3 rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4">
                <div className="flex items-center gap-2">
                  <Upload className="h-4 w-4 shrink-0 text-muted" aria-hidden="true" />
                  <label htmlFor="console-import-file" className="text-sm font-semibold text-primary">
                    Archivo JSON del export
                  </label>
                </div>
                <input
                  id="console-import-file"
                  ref={fileInputRef}
                  type="file"
                  accept=".json,application/json"
                  onChange={onFileChange}
                  disabled={busy}
                  aria-describedby="console-import-hint"
                  className="input-semantic block min-h-10 w-full cursor-pointer px-3 py-1.5 text-sm file:mr-3 file:cursor-pointer file:rounded-[var(--radius-sm)] file:border-0 file:bg-[var(--color-surface-3)] file:px-3 file:py-1 file:text-sm file:font-semibold file:text-primary"
                />
                <p id="console-import-hint" className="text-xs text-muted">
                  Solo <code>.json</code>, máximo 10 MiB y con un arreglo en la raíz. Se revisa en tu navegador antes de
                  enviarse; el archivo no se guarda en el servidor. {fileName ? `Archivo: ${fileName}.` : ""}
                </p>
              </div>

              {file && file.exceedsEntryLimit ? (
                <Alert variant="danger">
                  Este archivo trae {file.entries.length} entradas de consola y el servidor acepta hasta{" "}
                  {CONSOLE_IMPORT_MAX_ENTRIES} por importación: el commit es todo-o-nada, así que no se puede partir desde
                  aquí. No se ha descartado ninguna fila. Divide el export en partes de hasta {CONSOLE_IMPORT_MAX_ENTRIES}{" "}
                  entradas de consola (puedes conservar las filas de tienda) y repite la importación con cada parte.
                </Alert>
              ) : null}

              {file ? <ConsoleImportFileSummary file={file} /> : null}
            </div>
          ) : null}

          {step === "review" ? (
            <ConsoleImportReview
              entries={previewEntries}
              drafts={drafts}
              disabled={busy}
              onChangePlatform={onChangePlatform}
              onChangeAttach={onChangeAttach}
            />
          ) : null}

          {step === "result" && result !== null ? (
            <ConsoleImportResult result={result} titleByEntryId={titleByEntryId} />
          ) : null}
        </div>

        <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-default pt-3">
          <div className="text-xs text-muted" aria-live="polite">
            {step === "load" && file
              ? file.entries.length === 0
                ? "No hay ninguna fila de consola que importar en este archivo."
                : file.exceedsEntryLimit
                  ? `${file.entries.length} entradas de consola superan el tope de ${CONSOLE_IMPORT_MAX_ENTRIES} por importación.`
                  : `${file.entries.length} entradas listas para previsualizar.`
              : null}
            {step === "review"
              ? unresolvedCount > 0
                ? `${readyEntries.length} de ${previewEntries.length} se importarán; ${unresolvedCount} sin plataforma quedan fuera.`
                : `${readyEntries.length} entradas listas para importar.`
              : null}
            {step === "result" && result?.applied ? "Importación terminada: la biblioteca se recargó." : null}
            {step === "result" && result && !result.applied ? "No se escribió nada." : null}
          </div>

          <div className="flex flex-wrap items-center gap-2">
            {step === "load" ? (
              <Button
                type="button"
                variant="primary"
                loading={previewing}
                loadingText="Previsualizando..."
                disabled={file === null || file.entries.length === 0 || file.exceedsEntryLimit}
                onClick={() => void runPreview()}
              >
                Previsualizar {file ? file.entries.length : 0} entradas
              </Button>
            ) : null}

            {step === "review" ? (
              <>
                <Button type="button" variant="secondary" disabled={busy} onClick={() => setStep("load")}>
                  Volver al archivo
                </Button>
                <Button
                  type="button"
                  variant="primary"
                  loading={committing}
                  loadingText="Importando..."
                  disabled={readyEntries.length === 0}
                  onClick={() => void runCommit()}
                >
                  Importar {readyEntries.length} juegos
                </Button>
              </>
            ) : null}

            {step === "result" ? (
              <>
                {result && !result.applied ? (
                  <Button type="button" variant="secondary" disabled={busy} onClick={() => setStep("review")}>
                    Volver a revisar
                  </Button>
                ) : (
                  <Button type="button" variant="secondary" disabled={busy} onClick={reset}>
                    Importar otro archivo
                  </Button>
                )}
                <Button type="button" variant="primary" onClick={onClose}>
                  Cerrar
                </Button>
              </>
            ) : null}
          </div>
        </footer>
      </section>
    </div>
  );
}

function ConsoleImportResult({
  result,
  titleByEntryId
}: {
  readonly result: ConsoleImportCommitResult;
  readonly titleByEntryId: ReadonlyMap<string, string>;
}) {
  if (!result.applied) {
    return (
      <div className="space-y-3">
        <Alert variant="danger">
          El servidor rechazó la importación completa y no escribió nada: {result.conflicts.length}{" "}
          {result.conflicts.length === 1 ? "fila choca" : "filas chocan"} con la identidad de un juego ya fusionado.
        </Alert>
        <ul className="space-y-2">
          {result.conflicts.map((conflict) => (
            <li
              key={`${conflict.entryId}-${conflict.platform}`}
              className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3 text-xs"
            >
              <p className="text-sm font-semibold text-primary">
                {titleByEntryId.get(conflict.entryId) ?? conflict.entryId}
              </p>
              <p className="text-secondary">
                Plataforma {storeLabel(conflict.platform)}: el juego #{conflict.gameId} que elegiste pertenece al juego
                canónico #{conflict.ownerGameId}. Elige otro candidato o crea un juego nuevo en esa fila.
              </p>
            </li>
          ))}
        </ul>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className={cn("tabler-badge", result.created > 0 ? "tabler-badge-success" : "tabler-badge-muted")}>
          Juegos nuevos {result.created}
        </span>
        <span className={cn("tabler-badge", result.attached > 0 ? "tabler-badge-info" : "tabler-badge-muted")}>
          Enlazados {result.attached}
        </span>
        <span className={cn("tabler-badge", result.alreadyPresent > 0 ? "tabler-badge-warning" : "tabler-badge-muted")}>
          Ya estaban {result.alreadyPresent}
        </span>
      </div>
      <p className="text-xs text-muted">
        «Enlazados» comparten la ficha del juego elegido; «Ya estaban» no escribieron nada porque la fila exacta ya
        existía. La importación nunca borra ni actualiza filas: repetirla no duplica.
      </p>

      {result.entries.length > 0 ? (
        <ul className="space-y-1.5">
          {result.entries.map((entry) => (
            <li
              key={entry.entryId}
              className="flex flex-wrap items-center justify-between gap-2 rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-2"
            >
              <span className="min-w-0 text-sm font-semibold text-primary">
                {titleByEntryId.get(entry.entryId) ?? entry.entryId}
              </span>
              <span className="flex flex-wrap items-center gap-2">
                <span className="text-xs text-muted">{storeLabel(entry.platform)}</span>
                <span className={OUTCOME_BADGES[entry.outcome]}>{OUTCOME_LABELS[entry.outcome]}</span>
              </span>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
