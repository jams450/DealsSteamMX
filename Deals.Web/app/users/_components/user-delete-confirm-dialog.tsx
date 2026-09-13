import { Button } from "@/components/ui/button";
import type { AdminUser } from "@/lib/contracts/users-admin";

type Props = {
  user: AdminUser | null;
  open: boolean;
  loading: boolean;
  onCancel: () => void;
  onConfirm: () => void;
};

export function UserDeleteConfirmDialog({ user, open, loading, onCancel, onConfirm }: Props) {
  if (!open || !user) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-[100] flex items-center justify-center bg-[var(--color-overlay)] p-4 backdrop-blur-sm" role="presentation" onClick={onCancel}>
      <section
        className="w-full max-w-md p-4"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="delete-user-title"
        aria-describedby="delete-user-desc"
        onClick={(event) => event.stopPropagation()}
      >
        <p id="delete-user-title" className="text-primary text-sm font-semibold">
          ¿Borrar usuario?
        </p>
        <p id="delete-user-desc" className="text-muted mt-1 text-sm">
          Esta acción desactiva el usuario <span className="font-semibold">{user.email}</span> y no se puede revertir desde esta vista.
        </p>

        <div className="mt-4 flex justify-end gap-2">
          <Button type="button" variant="ghost" className="h-9 border-[var(--color-danger)]/50 bg-[var(--color-danger)]/15 text-[var(--color-danger)] hover:border-[var(--color-danger)]/70 hover:bg-[var(--color-danger)]/25" onClick={onCancel}>
            Cancelar
          </Button>
          <Button type="button" variant="danger" className="h-9" loading={loading} loadingText="Borrando..." onClick={onConfirm}>
            Borrar usuario
          </Button>
        </div>
      </section>
    </div>
  );
}
