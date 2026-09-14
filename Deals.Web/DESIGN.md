# DealExt Web — Design Contract

Single source of truth for tokens and primitives used by the web UI. Every color,
spacing value, radius and state in a component must trace back to a token defined here
or in `app/globals.css`. When a new value is needed, add the token (here + `globals.css`)
**before** using it. Do not hardcode hex/`rgb()` or arbitrary pixel offsets in components.

Tailwind v4 is configured in CSS (`@import "tailwindcss"` in `app/globals.css`); there is
no `tailwind.config.*`. Tokens are CSS custom properties consumed through semantic utility
classes (`.text-primary`, `.input-semantic`, `.table-*`, `.btn-*-semantic`).

## 0. Source Analysis (no greenfield research lanes)

This contract was extracted from the existing app, not invented:

- Token layer: `app/globals.css` (`:root`, `.dark`, `[data-theme="blue|light-blue"]`).
- Existing primitives: `components/ui/{button,input,alert,card,select}.tsx`,
  `lib/ui/cn.ts`, `lib/ui/table-action-styles.ts`, `components/data-grid/data-grid.tsx`.
- Existing patterns: semantic classes (`btn-*-semantic`, `input-semantic`, `table-*`,
  `app-card`, `tabler-panel`) instead of raw Tailwind color utilities.
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
   never rename/remove existing props or the `actions` column ID.
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

Price semantics reuse existing state tokens — no new colors for the first version:

| Signal | Token | Treatment |
|---|---|---|
| Best price / saving | `--color-success` | Badge + signed delta, never color alone |
| Price increase / unavailable | `--color-danger` | Badge + label |
| Stale or estimated data | `--color-warning` | Badge + explicit "estimado"/"desactualizado" text |
| Source freshness | `--color-info` | Muted meta line |

Store provenance is communicated by name/icon, not by a per-store color palette.

## 3. Typography

- Family: system stack (body in `globals.css`, `font-feature-settings: "cv11","ss01"`).
- Scale in use: `text-xs` (11–12px meta), `text-sm` (body/table), `text-base` (density
  "normal"), `text-lg+` (page headings via shell). Arbitrary `text-[10px]`/`text-[11px]`
  are allowed for uppercase meta labels only.
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
- Column IDs are a public contract. `actions` is the only ID consumed today; never rename
  or remove IDs an existing consumer already uses.
- Client sorting and client global filtering are built-in via `@tanstack/react-table`
  (`getSortedRowModel`, `getFilteredRowModel`). No new dependency.

### Panels / cards
- `.app-card` (surface-1 + radius-lg) for standalone cards and grouped content.
- `.tabler-panel` (surface-2 + radius-md) is retained for the existing toast stack.
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

## 7. Alcance v1 — Comparador de precios (Fase 0)

Product UI only, authenticated, backed by **local typed fixtures**. No real provider,
backend, persistence or pricing logic in this phase.

| Ruta | Objetivo | Datos |
|---|---|---|
| `/` | Home: buscar y explorar | fixtures |
| `/search?q=` | Resultados de búsqueda | fixtures |
| `/games/[steamAppId]` | Detalle + comparación por fuente | fixtures |
| `/watchlist` | Seguimiento (mock, sin persistencia) | fixtures locales |
| `/alerts` | Alertas (mock/coming-soon) | fixtures locales |
| `/users` | Admin existente (compatibilidad) | API real |

Estados mínimos por vista: `loading`, `empty`, `error`, `partial` (una fuente falla sin
ocultar las buenas) y `notFound` en detalle.

Fuera de alcance v1: integraciones reales (Steam/ITAD/gg.deals/FX), backend/EF/DB,
scraping, reglas de "mejor precio", conversión de moneda, bundles, persistencia de
watchlist/alerts, rediseño de `/login` y `/users`, i18n/multi-región, dependencias nuevas.

Decisiones pendientes antes de Fase 2: destino de `/steam`, destino post-login (hoy
`/users`), y contrato `Offer`/`ExternalBundle`/`SourceStatus`.

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

- `lib/ui/table-action-styles.ts` uses raw Tailwind tones (emerald/blue/amber) for action
  buttons; it violates principle 1 and should migrate to semantic tokens before being
  reused outside the admin Users slice.
- `DataGrid`'s `onGlobalFilterChange` always writes `internalGlobalFilter`, so a future
  server-side global filter needs dedicated wiring.
- No visual-regression/Lighthouse run was executed in this change: the checkout has no
  test project and the API/BFF is required for a live data render. Manual browser QA is
  still owed (see task report).
