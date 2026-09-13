import { useMemo } from "react";
import type { ColumnDef } from "@tanstack/react-table";
import { DataGrid } from "@/components/data-grid/data-grid";
import type { AdminUser } from "@/lib/contracts/users-admin";
import { getUserRoleBadgeClass, getUserRoleLabel, getUserStatusBadgeClass, getUserStatusLabel } from "../_lib/users-ui";
import { UserActionsMenu } from "./user-actions-menu";

type Props = {
  rows: AdminUser[];
  loading: boolean;
  errorMessage?: string | null;
  onEdit: (user: AdminUser) => void;
  onToggleActive: (user: AdminUser) => void;
  onDelete: (user: AdminUser) => void;
};

export function UsersTable({ rows, loading, errorMessage, onEdit, onToggleActive, onDelete }: Props) {
  const columns = useMemo<ColumnDef<AdminUser>[]>(
    () => [
      {
        accessorKey: "name",
        header: "Nombre",
        cell: ({ row }) => (
          <div className="space-y-0.5">
            <p className="text-primary text-sm font-bold">{row.original.name}</p>
            <p className="text-muted text-[11px] font-medium">ID #{row.original.userId}</p>
          </div>
        )
      },
      { accessorKey: "email", header: "Correo" },
      {
        accessorKey: "admin",
        header: "Rol",
        cell: ({ row }) => <span className={getUserRoleBadgeClass(row.original)}>{getUserRoleLabel(row.original)}</span>
      },
      {
        accessorKey: "active",
        header: "Estado",
        cell: ({ row }) => <span className={getUserStatusBadgeClass(row.original)}>{getUserStatusLabel(row.original)}</span>
      },
      {
        id: "actions",
        header: "",
        enableSorting: false,
        cell: ({ row }) => <UserActionsMenu user={row.original} onEdit={onEdit} onToggleActive={onToggleActive} onDelete={onDelete} />
      }
    ],
    [onDelete, onEdit, onToggleActive]
  );

  return (
    <div className="app-grid-skin overflow-hidden rounded-none p-0">
      <DataGrid
        columns={columns}
        rows={rows}
        loading={loading}
        errorMessage={errorMessage}
        emptyMessage="No hay usuarios con filtros actuales"
        density="compact"
        stickyHeader
        stickyActionsColumn
      />
    </div>
  );
}
