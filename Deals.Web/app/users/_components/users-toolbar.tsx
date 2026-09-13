import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { tableActionStyles } from "@/lib/ui/table-action-styles";
import { Plus, RotateCcw, Search, SlidersHorizontal } from "lucide-react";

type Props = {
  total: number;
  filtered: number;
  search: string;
  status: "all" | "active" | "inactive";
  role: "all" | "admin" | "user";
  hasActiveFilters: boolean;
  onSearchChange: (value: string) => void;
  onStatusChange: (value: "all" | "active" | "inactive") => void;
  onRoleChange: (value: "all" | "admin" | "user") => void;
  onResetFilters: () => void;
  onCreate: () => void;
};

export function UsersToolbar({
  total,
  filtered,
  search,
  status,
  role,
  hasActiveFilters,
  onSearchChange,
  onStatusChange,
  onRoleChange,
  onResetFilters,
  onCreate
}: Props) {
  return (
    <section className="p-0">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <span className="inline-flex h-7 w-7 items-center justify-center rounded-none border border-strong bg-[var(--color-surface-3)] text-primary">
            <SlidersHorizontal className="h-4 w-4" aria-hidden="true" />
          </span>
          <div>
            <p className="text-primary text-xs font-bold uppercase tracking-wide">Filtros</p>
            <p className="text-muted text-[11px] font-medium">
              Mostrando <span className="text-primary font-semibold">{filtered}</span> de <span className="text-primary font-semibold">{total}</span> usuarios
            </p>
          </div>
        </div>
        {hasActiveFilters ? (
          <Button type="button" variant="ghost" className="btn-secondary-semantic h-8 px-2.5 text-[11px] font-bold" onClick={onResetFilters}>
            <RotateCcw className="h-3.5 w-3.5" aria-hidden="true" />
            Limpiar (filtros activos)
          </Button>
        ) : null}
      </div>

      <div className="grid gap-2 p-2 lg:grid-cols-[1fr_150px_150px_auto] lg:items-end">
        <Input
          label="Buscar"
          value={search}
          onChange={(event) => onSearchChange(event.target.value)}
          placeholder="Nombre o correo"
          rightSlot={<Search className="text-muted h-3.5 w-3.5" aria-hidden="true" />}
        />

        <label className="text-secondary grid gap-1 text-xs font-medium uppercase tracking-wide">
          Estado
          <select
            value={status}
            onChange={(event) => onStatusChange(event.target.value as "all" | "active" | "inactive")}
            className="input-semantic h-8 rounded-none px-2.5 text-xs font-semibold"
          >
            <option value="all">Todos</option>
            <option value="active">Activos</option>
            <option value="inactive">Inactivos</option>
          </select>
        </label>

        <label className="text-secondary grid gap-1 text-xs font-medium uppercase tracking-wide">
          Rol
          <select
            value={role}
            onChange={(event) => onRoleChange(event.target.value as "all" | "admin" | "user")}
            className="input-semantic h-8 rounded-none px-2.5 text-xs font-semibold"
          >
            <option value="all">Todos</option>
            <option value="admin">Admin</option>
            <option value="user">Usuario</option>
          </select>
        </label>

        <Button
          type="button"
          variant="ghost"
          className={`h-8 rounded-md px-3 text-xs font-bold ${tableActionStyles.create}`}
          onClick={onCreate}
        >

          <Plus className="h-4 w-4" aria-hidden="true" />
          Nuevo usuario
        </Button>
      </div>
    </section>
  );
}
