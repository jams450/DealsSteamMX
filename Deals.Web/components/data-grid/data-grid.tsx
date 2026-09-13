"use client";

import {
  type ColumnDef,
  type FilterFn,
  type PaginationState,
  type SortingState,
  flexRender,
  getCoreRowModel,
  getFilteredRowModel,
  getPaginationRowModel,
  getSortedRowModel,
  useReactTable
} from "@tanstack/react-table";
import { ArrowDown, ArrowUp, ChevronsUpDown, Search, X } from "lucide-react";
import { type ReactNode, useEffect, useId, useState } from "react";
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
  toolbar?: ReactNode;
  stickyHeader?: boolean;
  stickyActionsColumn?: boolean;
  enableGlobalFilter?: boolean;
  globalFilterPlaceholder?: string;
  globalFilterFn?: FilterFn<TData>;
};

const pagerButtonClass =
  "btn-secondary-semantic h-7 px-2 text-[11px] disabled:opacity-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]";

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
  toolbar,
  stickyHeader = true,
  stickyActionsColumn = true,
  enableGlobalFilter = false,
  globalFilterPlaceholder = "Buscar...",
  globalFilterFn
}: DataGridProps<TData>) {
  const resolvedManualSorting = manualSorting ?? mode === "server";
  const resolvedManualPagination = manualPagination ?? mode === "server";

  const [internalSorting, setInternalSorting] = useState<SortingState>(initialSorting ?? []);
  const [internalPagination, setInternalPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 10 });
  const [internalDensity, setInternalDensity] = useState<DataGridDensity>(density ?? "compact");
  const [internalGlobalFilter, setInternalGlobalFilter] = useState("");
  const globalFilterInputId = useId();

  useEffect(() => {
    if (!densityStorageKey || density) {
      return;
    }

    const persistedDensity = window.localStorage.getItem(densityStorageKey);
    if (persistedDensity === "compact" || persistedDensity === "normal") {
      setInternalDensity(persistedDensity);
    }
  }, [density, densityStorageKey]);

  const effectiveSorting = sorting ?? internalSorting;
  const effectivePagination = pagination ?? internalPagination;
  const effectiveDensity = density ?? internalDensity;
  const effectiveGlobalFilter = enableGlobalFilter ? internalGlobalFilter : undefined;

  const table = useReactTable({
    data: rows,
    columns,
    state: {
      sorting: effectiveSorting,
      pagination: effectivePagination,
      globalFilter: effectiveGlobalFilter
    },
    manualSorting: resolvedManualSorting,
    manualPagination: resolvedManualPagination,
    rowCount,
    globalFilterFn,
    onGlobalFilterChange: (updater) => {
      const next = typeof updater === "function" ? updater(internalGlobalFilter) : updater;
      setInternalGlobalFilter(typeof next === "string" ? next : "");
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
    },
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: resolvedManualSorting ? undefined : getSortedRowModel(),
    getFilteredRowModel: enableGlobalFilter ? getFilteredRowModel() : undefined,
    getPaginationRowModel: resolvedManualPagination ? undefined : getPaginationRowModel()
  });

  const headerCellClass = cn(
    "text-left font-semibold text-primary",
    effectiveDensity === "compact" ? "px-2 py-2 text-xs" : "px-3 py-2.5 text-base"
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

      <div className="table-shell max-w-full overflow-x-auto overscroll-x-contain rounded-xl border border-strong bg-[var(--table-surface-bg)] shadow-[var(--shadow-sm)]">
        <table className="w-full min-w-full">
          <thead className="table-head bg-[var(--table-head-bg)]">
            {table.getHeaderGroups().map((headerGroup) => (
              <tr key={headerGroup.id}>
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

      {!resolvedManualPagination && table.getPageCount() > 1 ? (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-b-xl border-x border-b border-strong bg-[var(--table-surface-bg)] px-2 py-1.5">
          <div className="flex items-center gap-2">
            <span className="text-[11px] text-muted">Filas</span>
            <select
              value={table.getState().pagination.pageSize}
              onChange={(event) => table.setPageSize(Number(event.target.value))}
              className="input-semantic h-7 px-2 text-[11px]"
            >
              {pageSizeOptions.map((size) => (
                <option key={size} value={size}>
                  {size}
                </option>
              ))}
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
