import { CheckCircle2, CircleAlert, X } from "lucide-react";

export type UsersToast = {
  id: string;
  message: string;
  variant: "success" | "error";
};

type Props = {
  toasts: UsersToast[];
  onDismiss: (id: string) => void;
};

export function UsersToastStack({ toasts, onDismiss }: Props) {
  return (
    <div className="pointer-events-none fixed right-3 top-3 z-[120] flex w-[min(92vw,360px)] flex-col gap-2 md:right-6 md:top-5" aria-live="polite">
      {toasts.map((toast) => {
        const isSuccess = toast.variant === "success";
        return (
          <div
            key={toast.id}
            role="status"
            className={isSuccess
              ? "tabler-panel pointer-events-auto flex items-start gap-2.5 border-[var(--color-success)]/35 bg-[var(--color-surface-1)]/95 px-3 py-2.5 text-[var(--color-success)]"
              : "tabler-panel pointer-events-auto flex items-start gap-2.5 border-[var(--color-danger)]/35 bg-[var(--color-surface-1)]/95 px-3 py-2.5 text-[var(--color-danger)]"}
          >
            {isSuccess ? <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" /> : <CircleAlert className="mt-0.5 h-4 w-4 shrink-0" />}
            <p className="min-w-0 flex-1 text-sm font-medium">{toast.message}</p>
            <button
              type="button"
              className="text-muted hover:bg-[var(--color-accent-soft)] hover:text-primary rounded-md p-1 transition"
              onClick={() => onDismiss(toast.id)}
              aria-label="Cerrar notificación"
            >
              <X className="h-3.5 w-3.5" aria-hidden="true" />
            </button>
          </div>
        );
      })}
    </div>
  );
}
