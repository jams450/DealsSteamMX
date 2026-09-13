import type { ComponentProps, ReactNode } from "react";
import { cn } from "@/lib/ui/cn";

type InputProps = ComponentProps<"input"> & { label?: string; error?: string; rightSlot?: ReactNode };

export function Input({ label, className, error, rightSlot, id, "aria-describedby": ariaDescribedBy, ...props }: InputProps) {
  const fallbackId = label ? label.toLowerCase().replace(/\s+/g, "-") : undefined;
  const inputId = id ?? props.name ?? fallbackId ?? "input";
  const errorId = `${inputId}-error`;
  const describedBy = [ariaDescribedBy, error ? errorId : undefined].filter(Boolean).join(" ") || undefined;

  return (
    <div className="grid gap-1.5 text-sm font-medium text-[var(--color-text-primary)]">
      {label ? <label htmlFor={inputId}>{label}</label> : null}
      <div className="relative">
        <input
          id={inputId}
          className={cn("input-semantic h-10 w-full px-3 text-sm transition-shadow", rightSlot && "pr-24", error && "border-[var(--color-danger)] focus:border-[var(--color-danger)] focus:ring-2 focus:ring-[color:var(--color-danger)]", className)}
          aria-invalid={Boolean(error)}
          aria-describedby={describedBy}
          {...props}
        />
        {rightSlot ? <div className="absolute right-2 top-1/2 -translate-y-1/2">{rightSlot}</div> : null}
      </div>
      {error ? <span id={errorId} className="text-xs text-[var(--color-danger)]">{error}</span> : null}
    </div>
  );
}
