"use client";

// Reporte de lo que el archivo trae y de lo que no puede entrar (docs/PLAN_CONSOLE.md §7). Es la parte
// «mostrar inválidos/duplicados»: el parser deja fuera las filas que romperían el payload entero (un
// `Source` de tienda, un id repetido, un título de más de 256), y aquí se ven con su motivo antes de
// llamar al servidor. Nada de esto se envía.
import { CONSOLE_IMPORT_MAX_ENTRIES, CONSOLE_IMPORT_SKIP_LABELS, CONSOLE_IMPORT_SKIP_REASONS, type ConsoleImportFile, type ConsoleImportSkipReason } from "@/lib/contracts/console-import";
import { cn } from "@/lib/ui/cn";

// Tope de la lista desplegable: el reporte cuenta todo, pero no pinta mil filas.
const MAX_LISTED_SKIPS = 50;

type Props = { readonly file: ConsoleImportFile };

export function ConsoleImportFileSummary({ file }: Props) {
  const countsByReason = new Map<ConsoleImportSkipReason, number>();
  for (const skipped of file.skipped) {
    countsByReason.set(skipped.reason, (countsByReason.get(skipped.reason) ?? 0) + 1);
  }

  const reasons = CONSOLE_IMPORT_SKIP_REASONS.filter((reason) => countsByReason.has(reason));
  const listed = file.skipped.slice(0, MAX_LISTED_SKIPS);
  const hidden = file.skipped.length - listed.length;

  return (
    <div className="space-y-2" aria-live="polite">
      <div className="flex flex-wrap items-center gap-2">
        <span className="tabler-badge tabler-badge-muted">{file.totalRows} filas leídas</span>
        <span className={cn("tabler-badge", file.entries.length > 0 ? "tabler-badge-success" : "tabler-badge-muted")}>
          {file.entries.length} de consola
        </span>
        {file.skipped.length > 0 ? (
          <span className="tabler-badge tabler-badge-warning">{file.skipped.length} fuera del contrato</span>
        ) : null}
      </div>

      {reasons.length > 0 ? (
        <ul className="flex flex-wrap items-center gap-2">
          {reasons.map((reason) => (
            <li key={reason} className="tabler-badge tabler-badge-muted">
              {CONSOLE_IMPORT_SKIP_LABELS[reason]}: {countsByReason.get(reason) ?? 0}
            </li>
          ))}
        </ul>
      ) : null}

      {file.droppedDates > 0 ? (
        <p className="text-xs text-muted">
          {file.droppedDates} {file.droppedDates === 1 ? "entrada trae" : "entradas traen"} una fecha de alta ilegible: se
          importarán sin fecha en vez de rechazar el archivo entero.
        </p>
      ) : null}

      {file.exceedsEntryLimit ? (
        <p className="text-xs text-[var(--color-warning)]">
          {file.entries.length} entradas de consola superan el tope de {CONSOLE_IMPORT_MAX_ENTRIES} por importación:
          ninguna fila se descarta, pero este archivo no se puede importar de una vez.
        </p>
      ) : null}

      {file.skipped.length > 0 ? (
        <details className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3">
          <summary className="cursor-pointer text-xs font-semibold text-primary">
            Ver las entradas que no se importarán
          </summary>
          <ul className="mt-2 space-y-1">
            {listed.map((skipped, index) => (
              <li key={`${skipped.entryId ?? "sin-id"}-${index}`} className="text-xs text-secondary">
                <span className="font-semibold text-primary">{skipped.name ?? "(sin título)"}</span>
                {skipped.entryId ? <span className="ml-1 text-muted">· {skipped.entryId}</span> : null}
                {skipped.source ? <span className="ml-1 text-muted">· {skipped.source}</span> : null}
                <span className="ml-1">— {CONSOLE_IMPORT_SKIP_LABELS[skipped.reason]}</span>
              </li>
            ))}
          </ul>
          {hidden > 0 ? <p className="mt-1 text-xs text-muted">y {hidden} más.</p> : null}
        </details>
      ) : null}
    </div>
  );
}
