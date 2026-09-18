"use client";

import {
  type ColumnDef,
  type ColumnFiltersState,
  type FilterFn,
  type PaginationState,
  type SortingState,
  type VisibilityState,
  flexRender,
  getCoreRowModel,
  getFilteredRowModel,
  getPaginationRowModel,
  getSortedRowModel,
  useReactTable
} from "@tanstack/react-table";
import { ArrowDown, ArrowUp, ChevronsUpDown, Columns3, Search, X } from "lucide-react";
import { Fragment, type KeyboardEvent as ReactKeyboardEvent, type ReactNode, useEffect, useId, useRef, useState } from "react";
import { cn } from "@/lib/ui/cn";

export type DataGridMode = "client" | "server";
export type DataGridDensity = "compact" | "normal";

type DataGridProps<TData> = {
  columns: ColumnDef<TData>[];
  rows: TData[];
  mode?: DataGridMode;
  density?: DataGridDensity;
  allowDensityToggle?: boolean;
  densityStorageKey?: string;
  loading?: boolean;
  emptyMessage?: string;
  errorMessage?: string | null;
  manualSorting?: boolean;
  sorting?: SortingState;
  onSortingChange?: (sorting: SortingState) => void;
  manualPagination?: boolean;
  pagination?: PaginationState;
  onPaginationChange?: (pagination: PaginationState) => void;
  rowCount?: number;
  initialSorting?: SortingState;
  pageSizeOptions?: number[];
  // Añade la opción "Todos" al selector de filas. El valor real es enorme (MAX_SAFE_INTEGER), pero el
  // selector muestra la etiqueta, no el número.
  allowAllPageSize?: boolean;
  pageSizeStorageKey?: string;
  // Menú "Columnas". `initialColumnVisibility` solo aplica cuando no hay nada persistido, y sirve para
  // dejar una columna oculta por defecto sin sacarla del menú.
  enableColumnVisibility?: boolean;
  columnVisibilityStorageKey?: string;
  initialColumnVisibility?: VisibilityState;
  // Fila de filtros por columna. Sin esto, el grid queda exactamente como antes.
  enableColumnFilters?: boolean;
  toolbar?: ReactNode;
  stickyHeader?: boolean;
  stickyActionsColumn?: boolean;
  enableGlobalFilter?: boolean;
  globalFilterPlaceholder?: string;
  globalFilterFn?: FilterFn<TData>;
};

const pagerButtonClass =
  "btn-secondary-semantic h-7 px-2 text-[11px] disabled:opacity-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]";

const toolbarButtonClass =
  "btn-secondary-semantic inline-flex h-7 items-center gap-1 px-2 text-[11px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]";

// Valor interno de la opción "Todos": un pageSize que siempre cabe en una sola página.
const ALL_PAGE_SIZE = Number.MAX_SAFE_INTEGER;

// Filtro por defecto de las columnas: `includesString`, salvo que la columna traiga el suyo. Referencia
// estable para no recrear el objeto en cada render.
const DEFAULT_COLUMN_FILTER = { filterFn: "includesString" } as const;

export function DataGrid<TData>({
  columns,
  rows,
  mode = "client",
  density,
  allowDensityToggle = false,
  densityStorageKey,
  loading = false,
  emptyMessage = "Sin resultados",
  errorMessage,
  manualSorting,
  sorting,
  onSortingChange,
  manualPagination,
  pagination,
  onPaginationChange,
  rowCount,
  initialSorting,
  pageSizeOptions = [10, 25, 50],
  allowAllPageSize = false,
  pageSizeStorageKey,
  enableColumnVisibility = false,
  columnVisibilityStorageKey,
  initialColumnVisibility,
  enableColumnFilters = false,
  toolbar,
  stickyHeader = true,
  stickyActionsColumn = true,
  enableGlobalFilter = false,
  globalFilterPlaceholder = "Buscar...",
  globalFilterFn
}: DataGridProps<TData>) {
  const resolvedManualSorting = manualSorting ?? mode === "server";
  const resolvedManualPagination = manualPagination ?? mode === "server";
  const persistsPageSize = Boolean(pageSizeStorageKey) && !pagination && !resolvedManualPagination;

  const [internalSorting, setInternalSorting] = useState<SortingState>(initialSorting ?? []);
  const [internalPagination, setInternalPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 10 });
  const [internalDensity, setInternalDensity] = useState<DataGridDensity>(density ?? "compact");
  const [internalGlobalFilter, setInternalGlobalFilter] = useState("");
  const [internalColumnVisibility, setInternalColumnVisibility] = useState<VisibilityState>(initialColumnVisibility ?? {});
  const [columnFilters, setColumnFilters] = useState<ColumnFiltersState>([]);
  const [columnMenuOpen, setColumnMenuOpen] = useState(false);
  const globalFilterInputId = useId();
  const columnMenuRef = useRef<HTMLDivElement>(null);
  const columnMenuButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!densityStorageKey || density) {
      return;
    }

    const persistedDensity = window.localStorage.getItem(densityStorageKey);
    if (persistedDensity === "compact" || persistedDensity === "normal") {
      setInternalDensity(persistedDensity);
    }
  }, [density, densityStorageKey]);

  useEffect(() => {
    if (!enableColumnVisibility || !columnVisibilityStorageKey) {
      return;
    }

    const persisted = window.localStorage.getItem(columnVisibilityStorageKey);
    if (!persisted) {
      return;
    }

    try {
      const parsed = JSON.parse(persisted) as unknown;
      if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
        setInternalColumnVisibility(parsed as VisibilityState);
      }
    } catch {
      // Un valor corrupto no debe romper la tabla: se ignora y quedan las columnas por defecto.
    }
  }, [enableColumnVisibility, columnVisibilityStorageKey]);

  useEffect(() => {
    if (!persistsPageSize) {
      return;
    }

    const persisted = window.localStorage.getItem(pageSizeStorageKey as string);
    const parsed = Number(persisted);
    if (persisted && Number.isSafeInteger(parsed) && parsed > 0) {
      setInternalPagination((current) => ({ ...current, pageIndex: 0, pageSize: parsed }));
    }
  }, [persistsPageSize, pageSizeStorageKey]);

  useEffect(() => {
    if (!columnMenuOpen) {
      return;
    }

    function handlePointerDown(event: PointerEvent) {
      if (columnMenuRef.current && !columnMenuRef.current.contains(event.target as Node)) {
        setColumnMenuOpen(false);
      }
    }

    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  }, [columnMenuOpen]);

  const effectiveSorting = sorting ?? internalSorting;
  const effectivePagination = pagination ?? internalPagination;
  const effectiveDensity = density ?? internalDensity;
  const effectiveGlobalFilter = enableGlobalFilter ? internalGlobalFilter : undefined;
  // `getFilteredRowModel` sirve a la búsqueda global y a los filtros por columna: se activa con
  // cualquiera de los dos y sigue apagado cuando ninguno se usa.
  const filteringEnabled = enableGlobalFilter || enableColumnFilters;

  const table = useReactTable({
    data: rows,
    columns,
    state: {
      sorting: effectiveSorting,
      pagination: effectivePagination,
      globalFilter: effectiveGlobalFilter,
      columnVisibility: internalColumnVisibility,
      columnFilters
    },
    defaultColumn: enableColumnFilters ? DEFAULT_COLUMN_FILTER : undefined,
    manualSorting: resolvedManualSorting,
    manualPagination: resolvedManualPagination,
    rowCount,
    globalFilterFn,
    onGlobalFilterChange: (updater) => {
      const next = typeof updater === "function" ? updater(internalGlobalFilter) : updater;
      setInternalGlobalFilter(typeof next === "string" ? next : "");
    },
    onColumnFiltersChange: (updater) => {
      const next = typeof updater === "function" ? updater(columnFilters) : updater;
      setColumnFilters(next);
    },
    onColumnVisibilityChange: (updater) => {
      const next = typeof updater === "function" ? updater(internalColumnVisibility) : updater;
      setInternalColumnVisibility(next);
      if (columnVisibilityStorageKey) {
        try {
          window.localStorage.setItem(columnVisibilityStorageKey, JSON.stringify(next));
        } catch {
          // localStorage lleno o bloqueado: la tabla sigue funcionando en memoria.
        }
      }
    },
    onSortingChange: (updater) => {
      const next = typeof updater === "function" ? updater(effectiveSorting) : updater;
      if (onSortingChange) {
        onSortingChange(next);
      } else {
        setInternalSorting(next);
      }
    },
    onPaginationChange: (updater) => {
      const next = typeof updater === "function" ? updater(effectivePagination) : updater;
      if (onPaginationChange) {
        onPaginationChange(next);
      } else {
        setInternalPagination(next);
      }
      if (persistsPageSize) {
        window.localStorage.setItem(pageSizeStorageKey as string, String(next.pageSize));
      }
    },
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: resolvedManualSorting ? undefined : getSortedRowModel(),
    getFilteredRowModel: filteringEnabled ? getFilteredRowModel() : undefined,
    getPaginationRowModel: resolvedManualPagination ? undefined : getPaginationRowModel()
  });

  const headerCellClass = cn(
    "text-left font-semibold text-primary",
    effectiveDensity === "compact" ? "px-2 py-2 text-xs" : "px-3 py-2.5 text-base"
  );

  // Clase propia para la fila de filtros: `headerCellClass` ya trae `py`, y `cn` no resuelve
  // conflictos de Tailwind, así que no se puede recortar el padding sobre la misma cadena.
  const filterCellClass = cn(
    "text-left font-normal text-primary",
    effectiveDensity === "compact" ? "px-2 pb-2 text-xs" : "px-3 pb-2.5 text-base"
  );

  const bodyCellClass = cn(
    "text-primary",
    effectiveDensity === "compact" ? "px-2 py-2 text-xs" : "px-3 py-2.5 text-sm"
  );

  function handleDensityChange(nextDensity: DataGridDensity) {
    setInternalDensity(nextDensity);
    if (densityStorageKey) {
      window.localStorage.setItem(densityStorageKey, nextDensity);
    }
  }

  function columnLabel(columnId: string, header: unknown): string {
    return typeof header === "string" ? header : columnId;
  }

  function handleColumnMenuKeyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (event.key === "Escape") {
      setColumnMenuOpen(false);
      columnMenuButtonRef.current?.focus();
    }
  }

  const selectedPageSize = table.getState().pagination.pageSize;

  return (
      <div className="space-y-2">
      {allowDensityToggle && !density ? (
          <div className="flex items-center justify-end">
            <div className="inline-flex items-center gap-1 border border-strong bg-[var(--color-surface-2)] p-0.5">
            <button
              type="button"
              className={cn(
                "px-2 py-1 text-[11px] font-medium transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                effectiveDensity === "compact"
                  ? "bg-[var(--color-accent)] text-[var(--color-accent-contrast)]"
                  : "text-muted hover:bg-[var(--color-accent-soft)] hover:text-primary"
              )}
              onClick={() => handleDensityChange("compact")}
            >
              Compacta
            </button>
            <button
              type="button"
              className={cn(
                "px-2 py-1 text-[11px] font-medium transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]",
                effectiveDensity === "normal"
                  ? "bg-[var(--color-accent)] text-[var(--color-accent-contrast)]"
                  : "text-muted hover:bg-[var(--color-accent-soft)] hover:text-primary"
              )}
              onClick={() => handleDensityChange("normal")}
            >
              Cómoda
            </button>
          </div>
        </div>
      ) : null}

      {toolbar ? <div className="min-w-0">{toolbar}</div> : null}

      {enableGlobalFilter || enableColumnVisibility ? (
        <div className="flex flex-wrap items-center gap-2">
          {enableGlobalFilter ? (
            <div className="flex items-center">
              <label className="sr-only" htmlFor={globalFilterInputId}>
                {globalFilterPlaceholder}
              </label>
              <div className="relative w-full max-w-xs">
                <Search className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted" aria-hidden="true" />
                <input
                  id={globalFilterInputId}
                  type="search"
                  value={internalGlobalFilter}
                  onChange={(event) => setInternalGlobalFilter(event.target.value)}
                  placeholder={globalFilterPlaceholder}
                  className="input-semantic h-8 w-full pl-7 pr-7 text-xs"
                />
                {internalGlobalFilter ? (
                  <button
                    type="button"
                    aria-label="Limpiar búsqueda"
                    onClick={() => setInternalGlobalFilter("")}
                    className="absolute right-1 top-1/2 -translate-y-1/2 rounded-sm p-0.5 text-muted hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                  >
                    <X className="h-3.5 w-3.5" aria-hidden="true" />
                  </button>
                ) : null}
              </div>
            </div>
          ) : null}

          {enableColumnVisibility ? (
            <div className="relative ml-auto" ref={columnMenuRef} onKeyDown={handleColumnMenuKeyDown}>
              <button
                ref={columnMenuButtonRef}
                type="button"
                aria-haspopup="true"
                aria-expanded={columnMenuOpen}
                onClick={() => setColumnMenuOpen((open) => !open)}
                className={toolbarButtonClass}
              >
                <Columns3 className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                Columnas
              </button>
              {columnMenuOpen ? (
                <div
                  role="group"
                  aria-label="Columnas visibles"
                  className="absolute right-0 z-30 mt-1 min-w-40 rounded-[var(--radius-md)] border border-strong bg-[var(--color-surface-1)] p-1 shadow-[var(--shadow-md)]"
                >
                  {table
                    .getAllLeafColumns()
                    .filter((column) => column.id !== "actions" && column.getCanHide())
                    .map((column) => (
                      <label
                        key={column.id}
                        className="flex cursor-pointer items-center gap-2 px-2 py-1 text-xs text-secondary hover:text-primary"
                      >
                        <input
                          type="checkbox"
                          checked={column.getIsVisible()}
                          onChange={column.getToggleVisibilityHandler()}
                          className="h-3.5 w-3.5 accent-[var(--color-accent)]"
                        />
                        <span>{columnLabel(column.id, column.columnDef.header)}</span>
                      </label>
                    ))}
                </div>
              ) : null}
            </div>
          ) : null}
        </div>
      ) : null}

      <div className="table-shell max-w-full overflow-x-auto overscroll-x-contain rounded-xl border border-strong bg-[var(--table-surface-bg)] shadow-[var(--shadow-sm)]">
        <table className="w-full min-w-full">
          <thead className="table-head bg-[var(--table-head-bg)]">
            {table.getHeaderGroups().map((headerGroup) => (
              <Fragment key={headerGroup.id}>
                <tr>
                  {headerGroup.headers.map((header) => {
                    const canSort = header.column.getCanSort();
                    const sortState = header.column.getIsSorted();
                    const sortAriaValue = sortState === "asc" ? "ascending" : sortState === "desc" ? "descending" : "none";
                    const headerLabel =
                      typeof header.column.columnDef.header === "string" ? header.column.columnDef.header : header.column.id;
                    const isActionsColumn = header.column.id === "actions";
                    const stickyColumnClass =
                      stickyActionsColumn && isActionsColumn
                        ? "sticky right-0 z-10 bg-[var(--table-head-bg)]"
                        : undefined;
                    const stickyHeaderClass = stickyHeader ? "sticky top-0 z-20" : undefined;
                    const sortIndex = header.column.getSortIndex();
                    const showSortOrder = sortState && table.getState().sorting.length > 1;

                    return (
                      <th
                        key={header.id}
                        scope="col"
                        aria-sort={canSort ? sortAriaValue : undefined}
                        className={cn(headerCellClass, stickyHeaderClass, stickyColumnClass)}
                      >
                        {header.isPlaceholder ? null : canSort ? (
                          <button
                            type="button"
                            className="-mx-1 inline-flex cursor-pointer select-none items-center gap-1 rounded-sm px-1 hover:text-[var(--color-accent)] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
                            onClick={header.column.getToggleSortingHandler()}
                            aria-label={`Ordenar por ${headerLabel}`}
                          >
                            {flexRender(header.column.columnDef.header, header.getContext())}
                            {sortState === "asc" ? (
                              <ArrowUp className="h-3 w-3 text-[var(--color-accent)]" aria-hidden="true" />
                            ) : sortState === "desc" ? (
                              <ArrowDown className="h-3 w-3 text-[var(--color-accent)]" aria-hidden="true" />
                            ) : (
                              <ChevronsUpDown className="h-3 w-3 text-muted opacity-60" aria-hidden="true" />
                            )}
                            {showSortOrder ? <span className="text-[10px] text-muted">{sortIndex + 1}</span> : null}
                          </button>
                        ) : (
                          <span className="inline-flex items-center gap-1">
                            {flexRender(header.column.columnDef.header, header.getContext())}
                          </span>
                        )}
                      </th>
                    );
                  })}
                </tr>
                {/* Fila de filtros: no es sticky a propósito, el encabezado sticky (z-20, con fondo) la
                    tapa al hacer scroll en vez de superponerse a ella. */}
                {enableColumnFilters ? (
                  <tr>
                    {headerGroup.headers.map((header) => {
                      const column = header.column;
                      const isActionsColumn = column.id === "actions";
                      const stickyColumnClass =
                        stickyActionsColumn && isActionsColumn
                          ? "sticky right-0 z-10 bg-[var(--table-head-bg)]"
                          : undefined;
                      const filterable = column.getCanFilter() && column.id !== "cover" && !isActionsColumn;

                      return (
                        <th
                          key={`filter-${header.id}`}
                          scope="col"
                          className={cn(filterCellClass, stickyColumnClass)}
                        >
                          {filterable ? (
                            <input
                              type="search"
                              aria-label={`Filtrar por ${columnLabel(column.id, column.columnDef.header)}`}
                              placeholder="Filtrar"
                              value={String(column.getFilterValue() ?? "")}
                              onChange={(event) => column.setFilterValue(event.target.value)}
                              className="input-semantic h-7 w-full min-w-16 text-[11px]"
                            />
                          ) : null}
                        </th>
                      );
                    })}
                  </tr>
                ) : null}
              </Fragment>
            ))}
          </thead>
          <tbody>
            {errorMessage ? (
              <tr>
                <td className={cn(bodyCellClass, "text-[var(--color-danger)]")} colSpan={columns.length}>
                  {errorMessage}
                </td>
              </tr>
            ) : loading ? (
              <tr>
                <td className={cn(bodyCellClass, "text-muted")} colSpan={columns.length}>
                  Cargando...
                </td>
              </tr>
            ) : table.getRowModel().rows.length === 0 ? (
              <tr>
                <td className={cn(bodyCellClass, "text-muted")} colSpan={columns.length}>
                  {emptyMessage}
                </td>
              </tr>
              ) : (
                table.getRowModel().rows.map((row) => (
                <tr key={row.id} className="table-row transition">
                  {row.getVisibleCells().map((cell) => (
                    <td
                      key={cell.id}
                      className={cn(
                        bodyCellClass,
                        "table-cell",
                        stickyActionsColumn && cell.column.id === "actions" ? "sticky right-0 z-10 bg-[var(--table-surface-bg)]" : undefined
                      )}
                    >
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </td>
                  ))}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {!resolvedManualPagination && (table.getPageCount() > 1 || allowAllPageSize) ? (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-b-xl border-x border-b border-strong bg-[var(--table-surface-bg)] px-2 py-1.5">
          <div className="flex items-center gap-2">
            <span className="text-[11px] text-muted">Filas</span>
            <select
              value={selectedPageSize === ALL_PAGE_SIZE ? "all" : String(selectedPageSize)}
              onChange={(event) => table.setPageSize(event.target.value === "all" ? ALL_PAGE_SIZE : Number(event.target.value))}
              aria-label="Filas por página"
              className="input-semantic h-7 px-2 text-[11px]"
            >
              {pageSizeOptions.map((size) => (
                <option key={size} value={String(size)}>
                  {size}
                </option>
              ))}
              {allowAllPageSize ? <option value="all">Todos</option> : null}
            </select>
          </div>
          <div className="flex items-center gap-1.5">
            <button
              type="button"
              className={pagerButtonClass}
              onClick={() => table.previousPage()}
              disabled={!table.getCanPreviousPage()}
            >
              Anterior
            </button>
          <span className="text-[11px] text-muted">
            Página {table.getState().pagination.pageIndex + 1} de {table.getPageCount()}
          </span>
            <button
              type="button"
              className={pagerButtonClass}
              onClick={() => table.nextPage()}
              disabled={!table.getCanNextPage()}
            >
            Siguiente
          </button>
          </div>
        </div>
      ) : null}
    </div>
  );
}
