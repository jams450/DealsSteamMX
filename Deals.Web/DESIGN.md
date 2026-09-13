# DealExt Web — Design Contract

Single source of truth for tokens and primitives used by the web UI. Every color,
spacing value, radius and state in a component must trace back to a token defined here
or in `app/globals.css`. When a new value is needed, add the token (here + `globals.css`)
**before** using it. Do not hardcode hex/`rgb()` or arbitrary pixel offsets in components.

Tailwind v4 is configured in CSS (`@import "tailwindcss"` in `app/globals.css`); there is
no `tailwind.config.*`. Tokens are CSS custom properties consumed through semantic utility
classes (`.text-primary`, `.app-panel`, `.input-semantic`, `.table-*`, `.txn-*`).

## 0. Source Analysis (no greenfield research lanes)

This contract was extracted from the existing app, not invented:

- Token layer: `app/globals.css` (`:root`, `.dark`, `[data-theme="blue|light-blue"]`).
- Existing primitives: `components/ui/{button,input,alert,card,select}.tsx`,
  `lib/ui/cn.ts`, `lib/ui/table-action-styles.ts`, `components/data-grid/data-grid.tsx`.
- Existing patterns: semantic classes (`btn-*-semantic`, `input-semantic`, `table-*`,
  `app-panel`, `app-card`) instead of raw Tailwind color utilities.
- Theme starts dark by default (`app/layout.tsx` init script), so dark values are the
  primary surface and are tuned as such.
- No new external design reference or imagegen lane was run: the task is a reconciliation
  of the existing internal system (consistent, documented) rather than a net-new brand.

## 1. Principles

1. **No component without a token.** Color, spacing, radius, shadow and motion values are
   tokens, never one-off literals.
2. **Slate/warm neutrals, not pure black.** Dark surfaces use desaturated slate values so
   large data screens do not read as a void. Pure `#000`/`#090909` is forbidden for surfaces.
3. **Semantics over decoration.** Money direction is encoded by sign + token color, not by
   ornament. Motion only communicates a real state change.
4. **Additive compatibility.** Shared primitives (`DataGrid`) may gain optional props but
   never rename/remove existing props or column IDs — nine screens depend on them.
5. **Accessible by default.** Every interactive element exposes a visible keyboard focus
   state and a semantic role/label.

## 2. Color & Surfaces

Authoritative definitions live in `app/globals.css`. Summary of the contract:

| Token | Role |
|---|---|
| `--color-page-bg` | Application background base (gradient overlays sit on top) |
| `--color-surface-1` | Cards, primary elevated surfaces |
| `--color-surface-2` | Panels, table body surface |
| `--color-surface-3` | Headers, inset/zebra surface |
| `--color-border` / `--color-border-strong` | Hairline / emphasized borders |
| `--color-border-focus` | Focus ring + focused border |
| `--color-accent` / `--color-accent-soft` | Brand action / soft tint |
| `--color-success`, `--color-danger`, `--color-warning`, `--color-info` | Semantic states |

Dark surface tuning (this contract): slate/warm neutrals replacing near-black.
Light themes keep white/very light surfaces.

Transaction semantics reuse existing state tokens — no new colors:

| Transaction | Token | Treatment |
|---|---|---|
| `income` | `--color-success` | `+` amount, success badge/tint |
| `expense` / `opening_credit` | `--color-danger` | `−` amount, danger badge/tint |
| `transfer` | `--color-info` | unsigned neutral amount, info badge/tint |
| `opening_credit` (label) | `--color-warning` | warning badge, danger-signed amount (inherited debt) |

Encoded as reusable classes (in `globals.css`): `.txn-type-badge` + `.txn-type-income`,
`.txn-type-expense`, `.txn-type-transfer`, `.txn-type-opening`;
`.txn-amount` + `.txn-amount-income`, `.txn-amount-expense`, `.txn-amount-transfer`.
Components use these classes; they never re-declare the mixins inline.

## 3. Typography

- Family: system stack (body in `globals.css`, `font-feature-settings: "cv11","ss01"`).
- Scale in use: `text-xs` (11–12px meta), `text-sm` (body/table), `text-base` (density
  "normal"), `text-lg+` (page headings via shell). No ad-hoc `font-size` in px.
- Table numbers use `tabular-nums`; amounts are `font-semibold`.
- Headings/badges may use `uppercase tracking-wide` at `text-[10px]`/`text-[11px]`.

## 4. Spacing, Radius, Shadow

- Spacing on a 4px grid via Tailwind scale (`gap-1..4`, `p-2..4`). Page sections use
  `space-y-2`/`space-y-4`.
- Radius: `--radius-sm` (controls/buttons), `--radius-md` (panels/table shell),
  `--radius-lg` (cards/drawers). Full round only for status pills/badges.
- Shadow: `--shadow-sm` resting, `--shadow-md` raised/overlays. No bespoke box-shadows.

## 5. Primitives

### DataGrid (`components/data-grid/data-grid.tsx`)
- Props are additive-only. Existing: `columns, rows, mode, density, loading,
  emptyMessage, errorMessage, manualSorting, sorting, onSortingChange, manualPagination,
  pagination, onPaginationChange, rowCount, initialSorting, pageSizeOptions, toolbar,
  stickyHeader, stickyActionsColumn`.
- Added (optional, default off): `enableGlobalFilter`, `globalFilterPlaceholder`,
  `globalFilterFn`.
- Column IDs are a public contract (`actions`, `sharedExpense`, `categoryId`, …) — never
  renamed.
- Client sorting and client global filtering are built-in via `@tanstack/react-table`
  (`getSortedRowModel`, `getFilteredRowModel`). No new dependency.

### Panels / cards
- `.app-panel` (surface-2 + border + radius-md) for grouped content.
- `.app-card` (surface-1) for standalone cards.
- No hardcoded palette utilities (`bg-blue-50`, `border-blue-200`) for structural panels.

### Buttons / inputs
- `Button` variants: `primary | secondary | ghost | danger`, mapping to
  `.btn-*-semantic`. Inputs use `.input-semantic`.

## 6. Interaction States

- **Hover:** row `--color-accent` at 10% (`.table-row:hover`); buttons per variant class.
- **Focus (required):** `focus-visible:outline-none` + 2px ring in
  `var(--color-border-focus)`. Applies to sortable header buttons, filter input/clear,
  pagination, density toggles and section collapse toggles.
- **Active sort:** header exposes `aria-sort` and a direction icon
  (`ArrowUp`/`ArrowDown`/`ChevronsUpDown`), plus an ordinal when multi-sorting.
- **Loading:** row-level "Cargando..." text (existing behavior) — no layout shift.
- **Empty:** `emptyMessage` rendered in muted text, filter-aware copy from consumers.

## 7. Transaction History Layout

- Two independent client grids (regular transactions, transfer groups) inside collapsible
  `.app-panel` sections; state and callbacks preserved.
- **Type** column: `.txn-type-badge` pill with the semantic label.
- **Amount** column: `.txn-amount` with `+`/`−` sign and semantic color; transfers neutral.
- **Surfaces:** slate/warm neutrals; the near-black table shell is removed.
- Filters (type/account/category) plus in-grid text search (global filter) — both
  client-side.

## 8. Responsive & Containment

- Grid scroll container: `overflow-x-auto` + `overscroll-x-contain` + `max-w-full`, table
  `min-w-full`; horizontal overflow is contained inside the shell, never the page.
- History filter row: `grid` that is single-column on mobile and multi-column at `md`/`lg`.
- Panels/cells use `min-w-0` where flex children could otherwise force overflow.

## 9. Accessibility

- Sortable headers are real `<button>`s with `aria-label` ("Ordenar por <columna>") and
  `aria-sort` on the `<th>`.
- Global filter has an associated label (`sr-only` + `htmlFor`) and a labeled clear button.
- Section toggles are buttons with `aria-expanded` and `aria-controls`.
- Income/expense/transfer are distinguished by text sign + label in addition to color
  (never color alone).
- Focus is always visible on keyboard navigation.

## 10. Motion

- Transitions only on interactive feedback (`transition`, `transition-colors`) using
  default easing; GPU-safe properties (`color`, `opacity`, `background`, `transform`).
- No animated decoration on non-interactive elements. Layout-property animation forbidden.

## 11. Accepted Debt

- `use-history-columns.tsx` sorts lookup columns by resolved display name instead of raw
  ID (e.g. category name) and exposes the display value to the global filter. If a future
  server-side sort needs raw IDs, add explicit `sortFn`/`sortingFn` per column rather than
  reverting the accessor.
- Credit-status pill keeps its existing Tailwind emerald/amber/rose/slate tone classes
  (already consistent with the semantic palette); migrate to `.txn-*` tokens only if more
  statuses are introduced.
- No visual-regression/Lighthouse run was executed in this change: the checkout has no
  test project and the API/BFF is required for a live data render. Manual browser QA is
  still owed (see task report).
