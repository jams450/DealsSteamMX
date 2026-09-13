import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Alert } from "@/components/ui/alert";
import type { AdminUser, UserFormErrors, UserFormState } from "@/lib/contracts/users-admin";
import { X } from "lucide-react";

type Props = {
  open: boolean;
  user: AdminUser | null;
  form: UserFormState;
  errors: UserFormErrors;
  submitError: string | null;
  submitting: boolean;
  onClose: () => void;
  onChange: <K extends keyof UserFormState>(key: K, value: UserFormState[K]) => void;
  onSubmit: () => void;
};

export function UserFormDrawer({
  open,
  user,
  form,
  errors,
  submitError,
  submitting,
  onClose,
  onChange,
  onSubmit
}: Props) {
  if (!open) return null;

  const isEdit = Boolean(user);

  return (
    <div className="fixed inset-0 z-[70] flex items-end justify-end bg-[var(--color-overlay)] backdrop-blur-sm sm:items-stretch" role="presentation" onClick={onClose}>
      <section
        className="app-sidebar relative flex h-[100dvh] w-full flex-col border-l sm:h-full sm:max-w-xl"
        role="dialog"
        aria-modal="true"
        aria-labelledby="user-drawer-title"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="drawer-header-semantic">
          <div className="mb-1 h-1 w-12 bg-[var(--color-accent)]/70 sm:hidden" />
          <div className="flex items-start justify-between gap-3">
            <div>
              <h2 id="user-drawer-title" className="text-primary text-lg font-semibold">
                {isEdit ? "Editar usuario" : "Crear usuario"}
              </h2>
              <p className="text-muted text-xs">Admin puede crear, editar, activar y desactivar usuarios.</p>
            </div>
            <Button
              type="button"
              variant="ghost"
              className="btn-close-semantic"
              onClick={onClose}
            >
              <X className="h-3.5 w-3.5" aria-hidden="true" />
              <span>Cerrar</span>
            </Button>
          </div>
        </div>

        <div className="flex-1 space-y-4 overflow-y-auto px-4 py-4 sm:px-5 sm:py-5">
          <section className="drawer-section-semantic space-y-3">
            <p className="text-[11px] font-semibold uppercase tracking-wide text-blue-700 dark:text-blue-300">Datos obligatorios</p>
            <Input label="Nombre" value={form.name} onChange={(event) => onChange("name", event.target.value)} error={errors.name} />
            <Input label="Correo" type="email" value={form.email} onChange={(event) => onChange("email", event.target.value)} error={errors.email} />
            <Input
              label={isEdit ? "Nueva contraseña (opcional)" : "Contraseña"}
              type="password"
              value={form.password}
              onChange={(event) => onChange("password", event.target.value)}
              error={errors.password}
            />

            <div className="grid gap-2 sm:grid-cols-2">
            <label className="drawer-section-semantic text-secondary flex cursor-pointer items-center gap-2 text-sm">
              <input type="checkbox" checked={form.active} onChange={(event) => onChange("active", event.target.checked)} className="h-4 w-4 border-default bg-[var(--color-surface-1)] text-[var(--color-accent)] focus:ring-[var(--color-accent)]" />
              Usuario activo
            </label>
            <label className="drawer-section-semantic text-secondary flex cursor-pointer items-center gap-2 text-sm">
              <input type="checkbox" checked={form.admin} onChange={(event) => onChange("admin", event.target.checked)} className="h-4 w-4 border-default bg-[var(--color-surface-1)] text-[var(--color-accent)] focus:ring-[var(--color-accent)]" />
              Rol administrador
            </label>
            </div>
          </section>

          {submitError ? <Alert variant="danger">{submitError}</Alert> : null}
        </div>

        <div className="drawer-footer-semantic">
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" className="h-10 rounded-md border-[var(--color-danger)]/50 bg-[var(--color-danger)]/15 text-[var(--color-danger)] hover:border-[var(--color-danger)]/70 hover:bg-[var(--color-danger)]/25" onClick={onClose}>
              Cancelar
            </Button>
            <Button type="button" variant="ghost" className="h-10 rounded-md border-blue-400/60 bg-blue-500/15 text-blue-700 hover:border-blue-500/70 hover:bg-blue-500/25 hover:text-blue-800 dark:border-blue-700/60 dark:bg-blue-500/25 dark:text-blue-300 dark:hover:border-blue-500/70 dark:hover:bg-blue-500/35 dark:hover:text-blue-100" loading={submitting} loadingText="Guardando..." onClick={onSubmit}>
              {isEdit ? "Guardar cambios" : "Crear usuario"}
            </Button>
          </div>
        </div>
      </section>
    </div>
  );
}
