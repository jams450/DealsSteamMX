export const tableActionBaseClass = "h-8 border px-2 text-[11px] font-semibold";

export const tableActionStyles = {
  create: "border-emerald-400/60 bg-emerald-500/18 text-emerald-700 hover:border-emerald-500/70 hover:bg-emerald-500/28 hover:text-emerald-800 dark:border-emerald-700/60 dark:bg-emerald-500/25 dark:text-emerald-300 dark:hover:border-emerald-500/70 dark:hover:bg-emerald-500/35 dark:hover:text-emerald-100",
  edit: "border-blue-400/60 bg-blue-500/15 text-blue-700 hover:border-blue-500/70 hover:bg-blue-500/25 hover:text-blue-800 dark:border-blue-700/60 dark:bg-blue-500/25 dark:text-blue-300 dark:hover:border-blue-500/70 dark:hover:bg-blue-500/35 dark:hover:text-blue-100",
  deactivate: "border-amber-400/50 bg-amber-500/15 text-amber-200 hover:border-amber-300/70 hover:bg-amber-500/25",
  activate: "border-emerald-400/50 bg-emerald-500/15 text-emerald-200 hover:border-emerald-300/70 hover:bg-emerald-500/25",
  delete: "border-[var(--color-danger)]/50 bg-[var(--color-danger)]/15 text-[var(--color-danger)] hover:border-[var(--color-danger)]/70 hover:bg-[var(--color-danger)]/25"
} as const;
