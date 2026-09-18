import { CheckCircle2, CircleAlert, X } from "lucide-react";
import { cn } from "@/lib/ui/cn";

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
            className={cn(
              "app-card pointer-events-auto flex items-start gap-2.5 rounded-[var(--radius-md)] px-3 py-2.5",
              isSuccess ? "text-success" : "text-danger"
            )}
          >
            {isSuccess ? <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" /> : <CircleAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />}
            <p className="min-w-0 flex-1 text-sm font-medium">{toast.message}</p>
            <button
              type="button"
              className="text-muted hover:bg-[var(--color-accent-soft)] hover:text-[var(--color-text-primary)] rounded-[var(--radius-sm)] p-1 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)]"
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
