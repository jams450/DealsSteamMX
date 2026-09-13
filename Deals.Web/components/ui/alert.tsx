import { CircleAlert, Info } from "lucide-react";
import type { ComponentProps } from "react";
import { cn } from "@/lib/ui/cn";

type AlertVariant = "danger" | "info";
type AlertProps = ComponentProps<"div"> & { variant?: AlertVariant };

const variants: Record<AlertVariant, { className: string; Icon: typeof Info }> = {
  danger: { className: "border-[var(--color-danger)]/35 bg-[var(--color-danger)]/10 text-[var(--color-danger)]", Icon: CircleAlert },
  info: { className: "border-[var(--color-accent)]/35 bg-[var(--color-accent)]/10 text-[var(--color-accent)]", Icon: Info }
};

export function Alert({ className, variant = "info", children, ...props }: AlertProps) {
  const { className: variantClass, Icon } = variants[variant];
  return <div className={cn("flex items-start gap-2 rounded-[var(--radius-md)] border px-3 py-2.5 text-sm", variantClass, className)} role="alert" {...props}><Icon className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" /><div>{children}</div></div>;
}
