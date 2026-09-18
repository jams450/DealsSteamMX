import { cn } from "@/lib/ui/cn";

/*
 * Brand mark: a solid accent chip whose negative space forms a funnel — two
 * downward chevrons, the upper one wide (many stores) narrowing into the lower
 * one (one lowest price). Single path + `evenodd`, so the cut-outs are real
 * transparency (no mask/clip ids, safe to render many times on one page) and
 * the chip picks up whatever surface sits behind it.
 *
 * Geometry is authored in a 32x32 box and stays legible at 16px: cut thickness
 * is 5/32 units (~2.3px at favicon size) and every cut sits clear of the chip's
 * rounded corners. Keep `app/icon.svg` in sync with this path.
 */
const markPath =
  "M9 0H23A9 9 0 0 1 32 9V23A9 9 0 0 1 23 32H9A9 9 0 0 1 0 23V9A9 9 0 0 1 9 0Z" +
  "M7 11.3 16 15.3 25 11.3 25 6.3 16 10.3 7 6.3Z" +
  "M10.8 23.3 16 25.7 21.2 23.3 21.2 18.3 16 20.7 10.8 18.3Z";

type MarkProps = { className?: string };

/** Mark only. Decorative: the accessible name belongs to the brand link that wraps it. */
export function LogoMark({ className }: MarkProps) {
  return (
    <svg
      viewBox="0 0 32 32"
      className={cn("h-8 w-8 shrink-0 text-accent", className)}
      aria-hidden="true"
      focusable="false"
    >
      <path d={markPath} fill="currentColor" fillRule="evenodd" />
    </svg>
  );
}

type LogoProps = { className?: string; markClassName?: string };

/**
 * Full lockup: mark + wordmark. The three words are set as a two-line stack
 * ("Deals" over "Steam MX") so the long name stays narrow enough for the
 * 360px header. The visual text is decorative; the accessible name is the
 * exact product string.
 */
export function Logo({ className, markClassName }: LogoProps) {
  return (
    <span className={cn("inline-flex min-w-0 items-center gap-2", className)}>
      <LogoMark className={markClassName} />
      <span className="sr-only">Deals Steam MX</span>
      <span aria-hidden="true" className="flex min-w-0 flex-col leading-none">
        <span className="text-sm font-bold tracking-tight text-primary">Deals</span>
        <span className="mt-0.5 text-xs font-semibold uppercase tracking-[0.14em] text-muted">Steam MX</span>
      </span>
    </span>
  );
}
