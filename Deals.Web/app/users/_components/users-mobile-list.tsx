import type { AdminUser } from "@/lib/contracts/users-admin";
import { AlertCircle, Inbox } from "lucide-react";
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
      <div className="space-y-2.5 md:hidden">
        {Array.from({ length: 3 }).map((_, index) => (
          <div key={index} className="animate-pulse p-3">
            <div className="h-3 w-28 rounded-none bg-[var(--color-surface-3)]" />
            <div className="mt-2 h-2.5 w-44 rounded-none bg-[var(--color-surface-3)]" />
            <div className="mt-3 h-8 rounded-none bg-[var(--color-surface-3)]" />
          </div>
        ))}
      </div>
    );
  }

  if (errorMessage) {
    return (
      <div className="border-[var(--color-danger)]/35 bg-[var(--color-danger)]/12 p-4 text-[var(--color-danger)] md:hidden">
        <div className="flex items-start gap-2">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <div>
            <p className="text-sm font-bold">Error al cargar usuarios</p>
            <p className="mt-1 text-xs font-medium">{errorMessage}</p>
          </div>
        </div>
      </div>
    );
  }

  if (rows.length === 0) {
    return (
      <div className="border-default bg-[var(--color-surface-2)] px-3 py-8 text-center md:hidden">
        <Inbox className="text-muted mx-auto h-6 w-6" aria-hidden="true" />
        <p className="text-primary mt-2 text-sm font-bold">Sin resultados</p>
        <p className="text-muted mt-1 text-xs">No hay usuarios con filtros actuales.</p>
      </div>
    );
  }

  return (
    <div className="space-y-3 md:hidden">
      {rows.map((user) => (
        <article key={user.userId} className="p-3">
          <header className="flex items-start justify-between gap-2 pb-2">
            <div>
              <p className="text-primary text-sm font-extrabold">{user.name}</p>
              <p className="text-muted text-xs">{user.email}</p>
            </div>
            <p className="border-strong bg-[var(--color-surface-2)] text-secondary rounded-none border px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide">
              ID #{user.userId}
            </p>
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
