# DealExt Web — Design Contract

Single source of truth for tokens and primitives used by the DealExt product UI. Every color,
spacing value, radius and state in a component must trace back to a token defined here or in
`app/globals.css`. When a new value is needed, add the token (here + `globals.css`) **before**
using it. Do not hardcode hex/`rgb()` or arbitrary pixel offsets in components.

Tailwind v4 is configured in CSS (`@import "tailwindcss"` in `app/globals.css`); there is no
`tailwind.config.*`. Tokens are CSS custom properties consumed through semantic utility classes
(`.text-*`, `.app-card`, `.input-semantic`, `.table-*`, `.tabler-badge-*`).

## 0. Source analysis (extracted, not invented)

- Token layer: `app/globals.css` (`:root`, `.dark`, `[data-theme="blue|light-blue"]`).
- Primitives: `components/ui/{button,input,card,alert,select}.tsx`, `components/data-grid/data-grid.tsx`,
  `components/theme/theme-toggle.tsx`, `lib/ui/cn.ts`, `lib/bff/http.ts` (typed errors).
- Shells: `components/navigation/product-shell.tsx` (product surface) and
  `components/navigation/admin-shell.tsx` (admin surface, despite the file name — it is imported by
  `/users` and `/steam`).
- Patterns: semantic classes (`btn-*-semantic`, `input-semantic`, `table-*`, `app-card`,
  `tabler-badge-*`) instead of raw Tailwind palette utilities.
- Theme starts dark by default (`app/layout.tsx` init script, `localStorage.theme`), so dark values
  are the primary surface and are tuned as such.

## 1. Product scope and routes

DealExt is a game-deal finder: search a game, open its detail page, read the price, the discount and
when it was last observed.

| Route | Files | Surface | Notes |
|---|---|---|---|
| `/` | `app/page.tsx` | Product | Home: search as the central CTA, how-it-works, price source note |
| `/search` | `app/search/page.tsx`, `app/search/search-client.tsx` | Product | Text search; `?q=` pre-runs the query; local suggestions while typing (≥2 chars) |
| `/games/[steamAppId]` | `app/games/[steamAppId]/{page,game-client}.tsx` | Product | Offer detail: cover, price block, local-low row, source table |
| `/login` | `app/login/page.tsx` | Public | Only public page, plus `/api/auth/{login,refresh,session}` |
| `/users` | `app/users/*` | Admin | Reference admin slice (`AdminShell` + `DataGrid`), admin role only |
| `/steam` | `app/steam/*` | Product (legacy) | Earlier Steam page rendered inside `AdminShell`; not part of `ProductShell` |

- **Gate:** `middleware.ts` + `isPublicRoute` (`lib/security/route-policy.ts`) redirect any app route
  without a usable session to `/login?reason=session_expired`. There is currently no public product
  surface; "product" means the consumer-facing area (`/`, `/search`, `/games/*`), not anonymous access.
- **Scope of this contract:** `/`, `/search`, `/games/[steamAppId]` and `ProductShell`. `/users` and
  `/steam` keep `AdminShell` and their current layout.
- **Data reality:** the only price source is Steam (`/api/bff/steam/search`, `/api/bff/steam/games/[appId]`,
  `/api/bff/steam/suggestions`), MXN / Mexico region. There is **no multi-store aggregation**; the UI must not claim one or name
  stores that do not exist in the API. The "local low" (`lowestPriceMinor`/`lowestPriceAt`) is the lowest price
  observed locally in the app's own database, not a Steam-provided value, and it is never a discount.

## 2. Principles

1. **No component without a token.** Color, spacing, radius, shadow and motion values are tokens,
   never one-off literals.
2. **Dark-native graphite.** The default canvas is graphite with a blue brand signal, layered
   surfaces and restrained luminous edges. Light mode is a deliberate porcelain counterpart.
3. **Search-first.** Search is the primary action: it lives in the home hero, in the desktop header
   and in the mobile drawer, and it is always a native `GET /search?q=` form.
4. **Semantics over decoration.** Blue is brand/action only. Offer state is carried by
   success/warning/info/danger plus a text label — never color alone.
5. **Reuse before extend.** `app-card`, `Button`, `Input`, `Alert`, `ThemeToggle`, `.tabler-badge-*`
   and `.table-*` are consumed as-is; new tokens are added only when this UI consumes them.
6. **Accessible by default.** Visible keyboard focus, `aria-current` on the active route,
   `aria-expanded`/`aria-controls` on the drawer trigger, focus trap + `Escape` + focus return in the
   mobile drawer, `sr-only` table captions, labelled search inputs.
7. **Additive compatibility.** `ProductShell`, `Card`, `Button`, `Input`, `Alert` and `DataGrid` keep
   their existing props; nothing is renamed or removed for the sake of a restyle.

## 3. Color & surfaces

Graphite dark canvas (`#0d0f13`), graphite sidebar (`#14171c`), layered near-black surfaces
(`#1b1f26`, `#16191f`, `#23272f`), porcelain light (`#f1f5f9`) and blue signal (`#3b82f6` dark /
`#2563eb` light, with deeper hover ramp stops). Dark surfaces avoid pure black so borders, text and
blue focus states keep readable separation. Authoritative definitions live in `app/globals.css`;
light themes (`blue`, `light-blue`) are unchanged.

| Token | Role |
|---|---|
| `--color-page-bg` | App background base (gradient overlays sit on top) |
| `--color-page-grad-a` / `--color-page-grad-b` | Ambient brand radials behind `body` (blue, both themes) |
| `--color-surface-1` | Cards, primary elevated surfaces |
| `--color-surface-2` | Panels, table body surface |
| `--color-surface-3` | Headers, inset/zebra surface |
| `--color-border` / `--color-border-strong` | Hairline / emphasized borders |
| `--color-border-accent` | Accent-tinted rim (`app-card-accent`, brand mark) |
| `--color-border-focus` | Focus ring + focused border |
| `--color-accent` / `--color-accent-hover` / `--color-accent-soft` / `--color-accent-contrast` | Brand action / hover / soft tint / on-accent text |
| `--color-text-primary` / `--color-text-secondary` / `--color-text-muted` | Text hierarchy |
| `--color-success`, `--color-warning`, `--color-danger`, `--color-info` | Semantic states |
| `--color-overlay` | Mobile drawer scrim |
| `--radius-sm` / `--radius-md` / `--radius-lg` | Controls / panels / cards |
| `--shadow-sm` / `--shadow-md` | Resting / raised elevation |

Surfaces use a single-bezel recipe: hairline rim plus restrained shadow. Glow is limited to the
brand mark, the accent-rimmed card and the primary action. Shared semantic token names stay stable
across themes, so components never need theme-specific classes.

## 4. Offer semantics

| State | Meaning | Token | Class |
|---|---|---|---|
| Success | Best available price / price resolved | `--color-success` | `.tabler-badge-success`, `.text-success` |
| Warning | Incomplete price or state | `--color-warning` | `.tabler-badge-warning` |
| Info | Data / freshness metadata | `--color-info` | `.tabler-badge-info` |
| Danger | Error / price not available | `--color-danger` | `.tabler-badge-danger`, `.text-danger`, `Alert variant="danger"` |
| Accent | DealExt brand and actions | `--color-accent` | `.text-accent`, `.btn-primary-semantic`, `.border-accent` |

Text-color classes exist only where the product UI uses them (accent, success, danger); warning and
info states are always expressed as badges. Add the matching `.text-*` token the first time a
component needs it.

Rules currently implemented in `app/games/[steamAppId]/game-client.tsx`:

| Condition | Treatment |
|---|---|
| `currentPriceMinor` + `currency` present | price in `.deal-price` `.text-primary`, `tabler-badge-success` "Precio actual" |
| `isFree` | price reads "Gratis" in `.text-success`, no state badge |
| price missing | price in `.text-danger` ("Precio no disponible") + `tabler-badge-danger` "Sin precio" |
| `discountPercent` present | `tabler-badge-success` `-N%` and the base price switches to `.deal-price-strike` |
| `lowestPriceMinor` + `currency` present | `tabler-badge-info` "Mínimo observado localmente <precio> · <fecha>" |
| same low, and `currentPriceMinor <= lowestPriceMinor` (not free) | low badge tone becomes `tabler-badge-success`; the value is never expressed as a percentage or discount |
| `lowestPriceMinor` or `currency` missing | `tabler-badge-muted` "Sin mínimo observado localmente"; the table cell reads "—" |
| `initialPriceMinor` or `region` missing while a price exists | `tabler-badge-warning` "Datos incompletos" |
| `observedAt` present | `tabler-badge-info` "Actualizado <fecha>" |
| `observedAt` missing | `tabler-badge-warning` "Sin fecha de actualización" |
| fetch failure | `Alert variant="danger"` with the message and a link back to `/search` |

## 5. Typography

- Family: system stack (defined in `globals.css`, `font-feature-settings: "cv11","ss01"`). No font
  package is installed and no new dependency is allowed, so the DealExt voice comes from tracking,
  size and uppercase kickers rather than a display family.
- Scale in use: `text-xs` (kickers, meta), `text-sm` (body/table), `text-base` (card titles),
  `text-xl`/`text-2xl` (page title, via `ProductShell`), `text-2xl`/`text-3xl` (home headline and
  detail price). No ad-hoc `font-size` in px.
- Kickers use `uppercase tracking-widest` at `text-xs`.
- `.deal-price`: tabular numerals, bold, slight negative tracking. `.deal-price-strike`: muted,
  struck-through tabular numerals for the base price. Both live in `globals.css`.

## 6. Spacing, radius, shadow

- Spacing on the 4px grid via the Tailwind scale (`gap-1..5`, `p-2..6`).
- Radius: `--radius-sm` (controls), `--radius-md` (panels, tables, media), `--radius-lg` (cards,
  topbar, drawer). Full round only for status pills/badges.
- Shadow: `--shadow-sm` resting, `--shadow-md` raised/overlays. No bespoke box-shadows.

## 7. Primitives

### ProductShell (`components/navigation/product-shell.tsx`)

- Owns the product surface: sticky translucent header (`.app-topbar`), brand mark + `DealExt`
  wordmark, desktop navigation, header search, page title/subtitle, and the mobile drawer.
- Props are unchanged: `title`, `subtitle`, `meta`, `children`.
- Header search is a native `role="search"` form posting `GET /search?q=`. It is hidden below `lg`;
  the drawer renders the same form (id `shell-search-mobile`) for mobile.
- Active route: `data-active` styling is not used; the active link gets
  `.border-accent` + `.bg-[var(--color-accent-soft)]` and `aria-current="page"`.
- Mobile drawer: full-height `app-sidebar` panel, scrim, `role="dialog" aria-modal="true"`, focus
  trap, `Escape` to close, focus returned to the trigger, body scroll lock, and `overflow-y-auto`
  so the search + navigation + theme toggle always fit.

### Cards and surfaces

- `.app-card` — surface-1 + hairline border + radius-lg + resting shadow. Standalone cards.
- `.app-card-accent` — accent-tinted gradient rim + raised shadow. Used for the home hero and the
  game detail summary.
- `.app-topbar` — sticky, translucent, blurred shell header.
- `.table-shell` / `.table-head` / `.table-row` / `.table-cell` — table chrome; used by the game
  detail source table (and already by `DataGrid`).
- `SteamThumb` (local to `app/search/search-client.tsx`) — 120×45 `tiny_image` on `sm` and up,
  90×34 below, with a decorative `Gamepad2` placeholder when the URL is missing or fails to load
  (`onError`). The thumbnail is always decorative (`alt=""`) because the game name is adjacent text.
- Game detail cover: the API `imageUrl` inside `aspect-[460/215]`, full width on mobile and
  `md:w-72` on desktop. Missing or failed image falls back to the same aspect-ratio placeholder, so
  the header never collapses. Both covers keep the plain `<img>` debt noted in §11.

### Buttons / inputs / alerts

- `Button` variants: `primary | secondary | ghost | danger` → `.btn-*-semantic`. Inputs use
  `.input-semantic` (with `focus` ring from `--color-border-focus`).
- Links that act as buttons use the same `.btn-secondary-semantic` class (no `asChild` in `Button`).
- `Alert` variants: `danger | info`, `role="alert"`.

### Badges

`.tabler-badge` + tone: `tabler-badge-primary`, `tabler-badge-success`, `tabler-badge-warning`,
`tabler-badge-info` (added for data/freshness), `tabler-badge-danger`, `tabler-badge-muted`;
`tabler-badge-solid` for filled pills.

### DataGrid (`components/data-grid/data-grid.tsx`)

Admin-only today (`/users`). Props are additive-only; column IDs are a public contract. Available:
`columns, rows, mode, density, allowDensityToggle, densityStorageKey, loading, emptyMessage,
errorMessage, manualSorting, sorting, onSortingChange, manualPagination, pagination,
onPaginationChange, rowCount, initialSorting, pageSizeOptions, toolbar, stickyHeader,
stickyActionsColumn, enableGlobalFilter, globalFilterPlaceholder, globalFilterFn`.

## 8. Interaction states

- **Hover:** cards/rows per class; nav links and buttons use `--color-accent-soft` +
  `--color-border-accent`; `Button` variants per `.btn-*-semantic:hover`.
- **Focus (required):** `focus-visible:outline-none` + 2px ring in `var(--color-border-focus)` on
  every link, button, search input, nav item, drawer trigger and scrim.
- **Nav active:** accent border + soft accent background + `aria-current="page"`.
- **Drawer open:** trigger carries `aria-expanded` and `aria-controls="product-navigation-drawer"`.
- **Loading:** muted "Cargando..." text at the same size as the resolved content, so there is no
  layout shift (detail page and search skeleton both use `.app-card`/`text-sm text-muted`).
- **Suggestions:** local (BFF → DB) suggestions appear in an `.app-card` under the search input from
  2 typed characters, after a 300 ms debounce, and are cancelled when the query changes. They are
  hidden once an explicit search runs for the same term, and reappear when the query changes again.
  Each row is a `min-h-11` link to the detail page, so `Tab` alone reaches every suggestion. The
  results/empty/loading region is `aria-live="polite"`; the error message stays outside it with
  `role="alert"`. Steam search itself only runs on the button or `Enter`, never per keystroke.
- **External link:** the detail header and the table's "Fuente" cell link to
  `https://store.steampowered.com/app/<appId>/` with `target="_blank" rel="noopener noreferrer"` and
  an `sr-only` "(se abre en una pestaña nueva)" note next to the `ExternalLink` icon. The link is
  inside the "Fuente" cell on purpose: no extra column is added.
- **Empty:** muted "Sin resultados" inside an `.app-card` (search results).
- **Error:** `Alert variant="danger"` on the detail page; inline `.text-danger` `role="alert"` text in
  the search client.

## 9. Accessibility

- Search inputs have an accessible name (`aria-label`) and a unique `id` per instance
  (`shell-search`, `shell-search-mobile`, `home-search`) because the header and drawer forms can be
  mounted at the same time.
- Nav links: `aria-current="page"` on the active route; icons are `aria-hidden`.
- Drawer: `role="dialog"`, `aria-modal`, labelled close control, focus trap, `Escape`, focus
  restoration, body scroll lock.
- Detail table: `caption` with `sr-only` text; `th scope="col"`.
- Offer state is always carried by text ("Precio actual", "Datos incompletos", "Sin fecha de
  actualización") in addition to color.
- Form controls in the shell are 2.5rem (40px) tall — above the 24px WCAG 2.2 minimum, below the
  44px touch guideline (see Accepted debt).

## 10. Motion

- Only interactive feedback transitions (`transition-colors`) reusing the browser default timing;
  glow/hover changes are color/border/opacity only. No motion tokens currently exist in
  `globals.css`, and no autoplay/decorative motion is allowed.
- No layout-property animation is used by the product surface.

## 11. Accepted debt and limits

- **Header search duplication.** On `lg` and up the `/search` page shows the header search and the
  page's own input. Acceptable while search stays the primary action; collapse to one if the header
  search ever becomes the canonical control.
- **Control height.** Shell controls are 2.5rem (40px), inherited from the previous shell; the 44px
  touch guideline is not met on the product surface. Raise `.shell`-level control sizes only with a
  touch-audit requirement.
- **Cover image.** The game detail uses `<img>` with the Steam CDN URL because `next.config.ts` has
  no `images.remotePatterns`. This keeps the existing `@next/next/no-img-element` lint warning.
  Move to `next/image` only when a remote-pattern config lands. The URL itself is server-supplied
  (`imageUrl` from the API) and always degrades to the aspect-ratio placeholder.
- **Local low semantics.** `lowestPriceMinor` is the lowest price observed locally in this database
  for the row's currency; Steam does not provide it. It is presented as data only (badge + table
  cell), never as a discount, and it is not compared when the currency is missing.
- **Light theme.** The graphite retune touched `.dark` only; light themes are untouched and still
  legible, but they were not visually reviewed in this change.
- **Shared tokens.** The dark token retune is global, so `/users` and `/steam` (which render inside
  `AdminShell`) also shift to graphite. Their layout and components were not modified.
- **Not in scope:** multi-store comparison, price history charts, alerts/watchlists, currency
  switching. None of these have API contracts yet; do not imply them in copy. The local low is a
  single stored datum, not a history chart.
- **Verification.** No test project exists in this repo, so the check is `pnpm build` plus manual
  browser QA (owed: home/search/detail at light and dark, mobile drawer keyboard walkthrough).
  No Lighthouse or visual-regression run was executed.
