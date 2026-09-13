import type { AdminUser } from "@/lib/contracts/users-admin";
import { UsersMobileList } from "./users-mobile-list";
import { UsersTable } from "./users-table";

type Props = {
  rows: AdminUser[];
  loading: boolean;
  errorMessage?: string | null;
  onEdit: (user: AdminUser) => void;
  onToggleActive: (user: AdminUser) => void;
  onDelete: (user: AdminUser) => void;
};

export function UsersResults({ rows, loading, errorMessage, onEdit, onToggleActive, onDelete }: Props) {
  return (
    <section className="p-3 sm:p-4">
      <UsersMobileList rows={rows} loading={loading} errorMessage={errorMessage} onEdit={onEdit} onToggleActive={onToggleActive} onDelete={onDelete} />
      <div className="hidden md:block">
        <UsersTable rows={rows} loading={loading} errorMessage={errorMessage} onEdit={onEdit} onToggleActive={onToggleActive} onDelete={onDelete} />
      </div>
    </section>
  );
}
