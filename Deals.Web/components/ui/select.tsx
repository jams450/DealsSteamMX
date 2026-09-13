import { ChevronDown } from "lucide-react";
import type { ComponentProps } from "react";
import { cn } from "@/lib/ui/cn";

type SelectProps = ComponentProps<"select"> & { label?: string; error?: string };

export function Select({ label, error, className, id, "aria-describedby": ariaDescribedBy, children, ...props }: SelectProps) {
  const fallbackId = label ? label.toLowerCase().replace(/\s+/g, "-") : undefined;
  const selectId = id ?? props.name ?? fallbackId ?? "select";
  const errorId = `${selectId}-error`;
  const describedBy = [ariaDescribedBy, error ? errorId : undefined].filter(Boolean).join(" ") || undefined;

  return (
    <div className="grid gap-1.5 text-sm font-medium text-primary">
      {label ? <label htmlFor={selectId}>{label}</label> : null}
      <div className="relative">
        <select
          id={selectId}
          className={cn("input-semantic h-10 w-full appearance-none px-3 pr-9 text-sm transition-shadow", error && "border-[var(--color-danger)]", className)}
          aria-invalid={Boolean(error)}
          aria-describedby={describedBy}
          {...props}
        >
          {children}
        </select>
        <ChevronDown className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" aria-hidden="true" />
      </div>
      {error ? <span id={errorId} className="text-xs text-[var(--color-danger)]">{error}</span> : null}
    </div>
  );
}
