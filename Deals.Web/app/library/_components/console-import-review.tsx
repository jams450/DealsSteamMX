"use client";

// Paso «Revisar» de la importación masiva de consolas (docs/PLAN_CONSOLE.md §7). Cada fila resuelve DOS
// cosas a mano y nada más:
//   1. la plataforma que de verdad tienes (sugerida por Playnite, cambiada por el catálogo o escrita a
//      mano), que es la única que se escribe; y
//   2. la identidad: crear un juego nuevo (por defecto, la única acción que no afirma nada) o enganchar
//      la fila a un candidato que exista en el catálogo.
// El título nunca decide identidad y `Platforms` nunca decide posesión: por eso el borrador arranca en
// «crear» y la plataforma queda a la vista para confirmarla.
import { useEffect, useId, useMemo, useState } from "react";
import { Filter } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { cn } from "@/lib/ui/cn";
import {
  CONSOLE_IMPORT_PLATFORM_OTHER,
  consoleImportPlatformGroups,
  isConsoleImportDraftReady,
  resolveConsoleImportPlatform,
  type ConsoleImportCandidate,
  type ConsoleImportDecisionDraft,
  type ConsoleImportPreviewEntry
} from "@/lib/contracts/console-import";

const PAGE_SIZE = 25;
const RADIO_CLASS =
  "h-4 w-4 accent-[var(--color-accent)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]";

type FilterValue = "all" | "unresolved" | "candidates";

function candidateLabel(candidate: ConsoleImportCandidate): string {
  const year = candidate.releaseYear === null ? "" : ` (${candidate.releaseYear})`;
  const owned = candidate.inLibrary ? " — ya en tu biblioteca" : "";
  return `${candidate.title}${year}${owned}`;
}

// Un borrador ausente se lee como «sin plataforma»: la fila existe pero nadie eligió todavía.
function isEntryReady(
  drafts: ReadonlyMap<string, ConsoleImportDecisionDraft>,
  entryId: string
): boolean {
  const draft = drafts.get(entryId);
  return draft !== undefined && isConsoleImportDraftReady(draft);
}

type Props = {
  readonly entries: readonly ConsoleImportPreviewEntry[];
  readonly drafts: ReadonlyMap<string, ConsoleImportDecisionDraft>;
  readonly disabled: boolean;
  readonly onChangePlatform: (entryId: string, platform: string) => void;
  readonly onChangeAttach: (entryId: string, attachGameId: number | null) => void;
};

function ConsoleImportRow({ entry, draft, disabled, onChangePlatform, onChangeAttach }: {
  readonly entry: ConsoleImportPreviewEntry;
  readonly draft: ConsoleImportDecisionDraft;
  readonly disabled: boolean;
  readonly onChangePlatform: (entryId: string, platform: string) => void;
  readonly onChangeAttach: (entryId: string, attachGameId: number | null) => void;
}) {
  const domId = useId();
  const platformSelectId = `${domId}-platform`;
  const customInputId = `${domId}-custom`;
  const identityName = `${domId}-identity`;

  const groups = useMemo(() => consoleImportPlatformGroups(entry.suggestedPlatforms), [entry.suggestedPlatforms]);
  const optionValues = useMemo(
    () => new Set([...groups.suggested, ...groups.catalog].map((option) => option.value)),
    [groups]
  );

  // El valor del selector se deduce del borrador: "" = nada elegido todavía, la marca = texto libre, y
  // cualquier slug que no esté en la lista de opciones también se edita como texto libre.
  const typedFreeText = draft.platform !== "" && draft.platform !== CONSOLE_IMPORT_PLATFORM_OTHER && !optionValues.has(draft.platform);
  const selectValue = draft.platform === "" ? "" : typedFreeText || draft.platform === CONSOLE_IMPORT_PLATFORM_OTHER ? CONSOLE_IMPORT_PLATFORM_OTHER : draft.platform;
  const freeTextValue = typedFreeText ? draft.platform : "";

  const platform = resolveConsoleImportPlatform(draft);
  const ready = isConsoleImportDraftReady(draft);
  const playniteNames = entry.suggestedPlatforms.map((suggestion) => suggestion.source).join(", ");

  return (
    <li className="space-y-3 rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold text-primary">{entry.name}</p>
          <p className="truncate text-xs text-muted">
            {playniteNames ? `Playnite: ${playniteNames}` : "Sin plataforma reconocida por el catálogo"}
          </p>
        </div>
        {ready ? (
          <span className="tabler-badge tabler-badge-muted">{platform}</span>
        ) : (
          <span className="tabler-badge tabler-badge-warning">Falta plataforma</span>
        )}
      </div>

      <div className="flex flex-wrap items-end gap-2">
        <div className="w-full sm:w-64">
          <Select
            id={platformSelectId}
            label="Plataforma que tienes"
            value={selectValue}
            disabled={disabled}
            onChange={(event) => onChangePlatform(entry.entryId, event.target.value)}
          >
            <option value="" disabled>
              Elige una plataforma…
            </option>
            {groups.suggested.length > 0 ? (
              <optgroup label="Sugeridas por Playnite">
                {groups.suggested.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </optgroup>
            ) : null}
            <optgroup label="Consolas">
              {groups.catalog.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </optgroup>
            <option value={CONSOLE_IMPORT_PLATFORM_OTHER}>Otra plataforma…</option>
          </Select>
        </div>

        {selectValue === CONSOLE_IMPORT_PLATFORM_OTHER ? (
          <div className="min-w-40 flex-1 space-y-1.5">
            <label htmlFor={customInputId} className="text-sm font-medium text-primary">
              Tu plataforma
            </label>
            <input
              id={customInputId}
              type="text"
              value={freeTextValue}
              maxLength={32}
              disabled={disabled}
              autoComplete="off"
              placeholder="sega-saturn"
              onChange={(event) => onChangePlatform(entry.entryId, event.target.value)}
              className="input-semantic h-10 w-full px-3 text-sm"
            />
          </div>
        ) : null}
      </div>

      {entry.candidates.length === 0 ? (
        <p className="text-xs text-muted">Sin coincidencias en el catálogo: se creará un juego nuevo.</p>
      ) : (
        <fieldset className="space-y-1.5" disabled={disabled}>
          <legend className="text-xs font-semibold uppercase tracking-widest text-muted">Identidad</legend>
          <p className="text-xs text-muted">
            El título encontró {entry.candidates.length === 1 ? "un juego" : `${entry.candidates.length} juegos`} en el
            catálogo. Elige uno para compartir su ficha y su badge de posesión, o crea un juego nuevo. El título por sí
            solo no decide.
          </p>
          <label className="flex items-center gap-2 text-sm text-primary">
            <input
              type="radio"
              name={identityName}
              checked={draft.attachGameId === null}
              onChange={() => onChangeAttach(entry.entryId, null)}
              className={RADIO_CLASS}
            />
            Crear juego nuevo
          </label>
          <label className="flex items-center gap-2 text-sm text-primary">
            <input
              type="radio"
              name={identityName}
              checked={draft.attachGameId !== null}
              onChange={() => onChangeAttach(entry.entryId, entry.candidates[0]?.gameId ?? null)}
              className={RADIO_CLASS}
            />
            Es un juego del catálogo
          </label>
          {draft.attachGameId !== null ? (
            <div className="pl-6">
              <Select
                label="Juego del catálogo"
                value={String(draft.attachGameId)}
                disabled={disabled}
                onChange={(event) => onChangeAttach(entry.entryId, Number(event.target.value))}
              >
                {entry.candidates.map((candidate) => (
                  <option key={candidate.gameId} value={candidate.gameId}>
                    {candidateLabel(candidate)}
                  </option>
                ))}
              </Select>
            </div>
          ) : null}
        </fieldset>
      )}
    </li>
  );
}

export function ConsoleImportReview({ entries, drafts, disabled, onChangePlatform, onChangeAttach }: Props) {
  const [filter, setFilter] = useState<FilterValue>("all");
  const [page, setPage] = useState(0);
  const [bulkPlatform, setBulkPlatform] = useState("");
  const [bulkCustom, setBulkCustom] = useState("");
  const bulkId = useId();

  const unresolvedCount = useMemo(
    () => entries.filter((entry) => !isEntryReady(drafts, entry.entryId)).length,
    [drafts, entries]
  );
  const candidateCount = useMemo(() => entries.filter((entry) => entry.candidates.length > 0).length, [entries]);

  const filtered = useMemo(() => {
    if (filter === "candidates") return entries.filter((entry) => entry.candidates.length > 0);
    if (filter === "unresolved") return entries.filter((entry) => !isEntryReady(drafts, entry.entryId));
    return entries;
  }, [drafts, entries, filter]);

  const pageCount = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE));

  // Al filtrar o al reducirse la lista, la página puede quedar fuera de rango: se recorta en vez de
  // pintar una página vacía sin explicación.
  useEffect(() => {
    setPage((current) => Math.min(current, pageCount - 1));
  }, [pageCount]);

  const pageEntries = filtered.slice(page * PAGE_SIZE, page * PAGE_SIZE + PAGE_SIZE);
  const readyCount = entries.length - unresolvedCount;

  // Plataforma en bloque para las filas que Playnite no supo resolver: escribir la misma consola 200
  // veces no es una decisión, es trabajo repetido. Sigue siendo una elección explícita de la persona.
  const bulkResolved = bulkPlatform === CONSOLE_IMPORT_PLATFORM_OTHER ? bulkCustom.trim().toLowerCase() : bulkPlatform;
  const bulkOptions = useMemo(() => consoleImportPlatformGroups([]).catalog, []);

  function applyBulk() {
    if (bulkResolved.length === 0) return;
    for (const entry of entries) {
      if (isEntryReady(drafts, entry.entryId)) continue;
      onChangePlatform(entry.entryId, bulkResolved);
    }
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="tabler-badge tabler-badge-success">
          {readyCount} / {entries.length} listas
        </span>
        {unresolvedCount > 0 ? (
          <span className="tabler-badge tabler-badge-warning">{unresolvedCount} sin plataforma</span>
        ) : null}
        {candidateCount > 0 ? (
          <span className="tabler-badge tabler-badge-info">{candidateCount} con coincidencias</span>
        ) : null}
      </div>

      <div className="flex flex-wrap items-end gap-2">
        <div className="flex items-center gap-2">
          <Filter className="h-4 w-4 text-muted" aria-hidden="true" />
          <span className="text-xs font-semibold uppercase tracking-widest text-muted">Filtro</span>
        </div>
        <div className="flex flex-wrap items-center gap-1 border border-strong bg-[var(--color-surface-2)] p-0.5" role="group" aria-label="Filtrar entradas">
          {([
            ["all", `Todas (${entries.length})`],
            ["unresolved", `Sin plataforma (${unresolvedCount})`],
            ["candidates", `Con coincidencias (${candidateCount})`]
          ] as const).map(([value, label]) => (
            <button
              key={value}
              type="button"
              aria-pressed={filter === value}
              onClick={() => {
                setFilter(value);
                setPage(0);
              }}
              className={cn(
                "h-8 px-3 text-xs font-medium transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                filter === value
                  ? "bg-[var(--color-accent)] text-[var(--color-accent-contrast)]"
                  : "text-muted hover:bg-[var(--color-accent-soft)] hover:text-primary"
              )}
            >
              {label}
            </button>
          ))}
        </div>
      </div>

      {unresolvedCount > 0 ? (
        <div className="flex flex-wrap items-end gap-2 rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3">
          <div className="w-full sm:w-56">
            <Select
              id={`${bulkId}-bulk-platform`}
              label="Asignar en bloque a las pendientes"
              value={bulkPlatform}
              disabled={disabled}
              onChange={(event) => setBulkPlatform(event.target.value)}
            >
              <option value="" disabled>
                Elige una plataforma…
              </option>
              <optgroup label="Consolas">
                {bulkOptions.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </optgroup>
              <option value={CONSOLE_IMPORT_PLATFORM_OTHER}>Otra plataforma…</option>
            </Select>
          </div>
          {bulkPlatform === CONSOLE_IMPORT_PLATFORM_OTHER ? (
            <div className="min-w-40 flex-1 space-y-1.5">
              <label htmlFor={`${bulkId}-bulk-custom`} className="text-sm font-medium text-primary">
                Tu plataforma
              </label>
              <input
                id={`${bulkId}-bulk-custom`}
                type="text"
                value={bulkCustom}
                maxLength={32}
                disabled={disabled}
                autoComplete="off"
                placeholder="sega-saturn"
                onChange={(event) => setBulkCustom(event.target.value)}
                className="input-semantic h-10 w-full px-3 text-sm"
              />
            </div>
          ) : null}
          <Button
            type="button"
            variant="secondary"
            className="h-10 whitespace-nowrap px-3 text-xs"
            disabled={disabled || bulkResolved.length === 0}
            onClick={applyBulk}
          >
            Aplicar a las {unresolvedCount} pendientes
          </Button>
        </div>
      ) : null}

      <ul className="space-y-2">
        {pageEntries.map((entry) => {
          const draft =
            drafts.get(entry.entryId) ??
            ({ entryId: entry.entryId, name: entry.name, platform: "", attachGameId: null } satisfies ConsoleImportDecisionDraft);
          return (
            <ConsoleImportRow
              key={entry.entryId}
              entry={entry}
              draft={draft}
              disabled={disabled}
              onChangePlatform={onChangePlatform}
              onChangeAttach={onChangeAttach}
            />
          );
        })}
      </ul>

      {filtered.length === 0 ? (
        <p className="rounded-[var(--radius-sm)] border border-default bg-[var(--color-surface-2)] p-3 text-xs text-muted">
          Ninguna entrada coincide con el filtro.
        </p>
      ) : pageCount > 1 ? (
        <div className="flex flex-wrap items-center justify-between gap-2">
          <Button
            type="button"
            variant="secondary"
            className="h-9 px-3 text-xs"
            disabled={disabled || page === 0}
            onClick={() => setPage((current) => Math.max(0, current - 1))}
          >
            Anterior
          </Button>
          <span className="text-xs tabular-nums text-muted" aria-live="polite">
            Página {page + 1} de {pageCount} · {filtered.length} entradas
          </span>
          <Button
            type="button"
            variant="secondary"
            className="h-9 px-3 text-xs"
            disabled={disabled || page >= pageCount - 1}
            onClick={() => setPage((current) => Math.min(pageCount - 1, current + 1))}
          >
            Siguiente
          </Button>
        </div>
      ) : null}
    </div>
  );
}
