import type { AdminUser } from "@/lib/contracts/users-admin";
import { Inbox } from "lucide-react";
import { Alert } from "@/components/ui/alert";
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

export function UsersMobileList({ rows, loading, errorMessage, onEdit, onToggleActive, onDelete }: Props) {
  if (loading) {
    return (
      <div className="space-y-3 md:hidden">
        {Array.from({ length: 3 }).map((_, index) => (
          <div key={index} className="app-card animate-pulse rounded-[var(--radius-md)] p-3">
            <div className="h-3 w-28 rounded-[var(--radius-sm)] bg-[var(--color-surface-3)]" />
            <div className="mt-2 h-2.5 w-44 rounded-[var(--radius-sm)] bg-[var(--color-surface-3)]" />
            <div className="mt-3 h-8 rounded-[var(--radius-sm)] bg-[var(--color-surface-3)]" />
          </div>
        ))}
      </div>
    );
  }

  if (errorMessage) {
    return (
      <div className="md:hidden">
        <Alert variant="danger">
          <p className="text-sm font-semibold">Error al cargar usuarios</p>
          <p className="mt-1 text-xs font-medium">{errorMessage}</p>
        </Alert>
      </div>
    );
  }

  if (rows.length === 0) {
    return (
      <div className="app-card rounded-[var(--radius-md)] px-3 py-8 text-center md:hidden">
        <Inbox className="text-muted mx-auto h-6 w-6" aria-hidden="true" />
        <p className="text-primary mt-2 text-sm font-bold">Sin resultados</p>
        <p className="text-muted mt-1 text-xs">No hay usuarios con filtros actuales.</p>
      </div>
    );
  }

  return (
    <div className="space-y-3 md:hidden">
      {rows.map((user) => (
        <article key={user.userId} className="app-card rounded-[var(--radius-md)] p-3">
          <header className="flex items-start justify-between gap-2 pb-2">
            <div className="min-w-0">
              <p className="text-primary text-sm font-extrabold">{user.name}</p>
              <p className="text-muted text-xs">{user.email}</p>
            </div>
            <span className="tabler-badge tabler-badge-muted">ID #{user.userId}</span>
          </header>

          <div className="flex items-center gap-2 py-2">
            <span className={getUserRoleBadgeClass(user)}>{getUserRoleLabel(user)}</span>
            <span className={getUserStatusBadgeClass(user)}>{getUserStatusLabel(user)}</span>
          </div>

          <footer>
            <UserActionsMenu user={user} mobile onEdit={onEdit} onToggleActive={onToggleActive} onDelete={onDelete} />
          </footer>
        </article>
      ))}
    </div>
  );
}
