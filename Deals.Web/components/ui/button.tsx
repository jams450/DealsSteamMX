import { forwardRef, type ComponentProps } from "react";
import { cn } from "@/lib/ui/cn";

type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";
type ButtonProps = ComponentProps<"button"> & { variant?: ButtonVariant; fullWidth?: boolean; loading?: boolean; loadingText?: string };

const variantClass: Record<ButtonVariant, string> = {
  primary: "btn-primary-semantic disabled:border-[var(--color-border-strong)] disabled:bg-[var(--color-border-strong)]",
  secondary: "btn-secondary-semantic",
  ghost: "btn-ghost-semantic",
  danger: "btn-danger-semantic hover:brightness-95"
};

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button({ className, variant = "primary", fullWidth, loading, loadingText, children, disabled, ...props }, ref) {
  return <button ref={ref} className={cn("inline-flex h-10 items-center justify-center gap-2 rounded-[var(--radius-sm)] px-4 text-sm font-semibold transition-colors", "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[color:var(--color-border-focus)] focus-visible:ring-offset-2 focus-visible:ring-offset-[var(--color-page-bg)]", "disabled:cursor-not-allowed disabled:opacity-60", fullWidth && "w-full", variantClass[variant], className)} disabled={disabled || loading} aria-busy={loading || undefined} {...props}>{loading ? <><span className="h-4 w-4 animate-spin rounded-full border-2 border-current/30 border-t-current" aria-hidden="true" />{loadingText ?? "Cargando..."}</> : children}</button>;
});
