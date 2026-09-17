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
| `/games/[steamAppId]` | `app/games/[steamAppId]/{page,game-client}.tsx` | Product | Offer detail: cover, Steam price block, local-low row, Steam source table, multi-store offers (official stores only in v1) |
| `/login` | `app/login/page.tsx` | Public | Only public page, plus `/api/auth/{login,refresh,session}` |
| `/users` | `app/users/*` | Admin | Reference admin slice (`AdminShell` + `DataGrid`), admin role only |
| `/steam` | `app/steam/*` | Product (legacy) | Earlier Steam page rendered inside `AdminShell`; not part of `ProductShell` |

- **Gate:** `middleware.ts` + `isPublicRoute` (`lib/security/route-policy.ts`) redirect any app route
  without a usable session to `/login?reason=session_expired`. There is currently no public product
  surface; "product" means the consumer-facing area (`/`, `/search`, `/games/*`), not anonymous access.
- **Scope of this contract:** `/`, `/search`, `/games/[steamAppId]` and `ProductShell`. `/users` and
  `/steam` keep `AdminShell` and their current layout.
- **Data reality:** two sources feed the detail page. The regional price is Steam
  (`/api/bff/steam/search`, `/api/bff/steam/games/[appId]`, `/api/bff/steam/suggestions`), MXN / Mexico
  region. Cross-store offers ride on the same game payload (`offers`); the explicit refresh uses
  `POST /api/bff/steam/games/[appId]` and comes from IsThereAnyDeal. The provider-neutral contract keeps
  `classification` for future sources, but v1 renders **official stores only** (single
  group) and drops authorized rows at the UI boundary. ITAD does not cover keyshops or grey market,
  so the UI never claims "all stores" and never uses the word "keys".
  ITAD returns each offer in the store's own currency (`original*` fields, the source of truth); the
  MXN columns (`mxn*`) are derived from the day's FX rate (Banxico FIX, Frankfurter fallback),
  nullable, and always presented as approximate. Offers are a per-shop snapshot, not history. The
  "local low" (`lowestPriceMinor`/`lowestPriceAt`) is the lowest price observed locally in the app's
  own database, not a Steam-provided value, and it is never a discount.

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

Rules currently implemented for the multi-store offers section (same file). External data is treated
as untrusted: the normalizer in `lib/contracts/steam.ts` drops what it cannot validate instead of
rendering it.

| Condition | Treatment |
|---|---|
| `classification === "official"` | rendered as the only group, under the `Tiendas oficiales` heading; the shop cell also carries `tabler-badge-info` "Oficial" |
| `classification !== "official"` (authorized) | not rendered in v1; filtered out before grouping, counting and empty-state decisions |
| a group has no rows | the group's heading and table are not rendered at all |
| `pricingType === "regional"` | `Aprox. MXN` shows the stored MXN snapshot plainly (no `≈`) |
| `pricingType === "fx_estimate"` | `Aprox. MXN` is prefixed `≈` and carries `Tasa <rate> · <dd/MM/yyyy> · <source>` underneath |
| `pricingType === "unconverted"` | `tabler-badge-warning` "Sin conversión" in `Aprox. MXN`; no MXN value is invented |
| `pricingType` missing or unknown | the whole offer is invalid and is dropped (never silently mapped to `unconverted`) |
| any required offer field missing (`source`, `offerKey`, `shopName`, `classification`, `originalCurrency`) | the offer is dropped |
| `mxnCurrentPriceMinor` missing | `Aprox. MXN` reads "—" |
| `originalCurrentPriceMinor === 0` | `Precio` reads "Gratis" in `.text-success` |
| `originalCurrentPriceMinor` missing | `Precio` reads "—" in `.text-muted` |
| `discountPercent` present | `tabler-badge-success` `-N%` in `Descuento`; `Precio base` switches to `.deal-price-strike` |
| `dealUrl` present as an absolute `https:` URL | the shop name is the external link described in §8; the URL is used verbatim, affiliate tag included |
| `dealUrl` missing, not a string, not absolute, or not `https:` | the shop name is plain text (the value is ignored, never rewritten) |
| `observedAt` invalid or missing | `Observado` reads "—" |
| `offersStale` | `tabler-badge-warning` "Datos posiblemente desactualizados" |
| `offersRefreshedAt` missing or invalid while official offers exist | `tabler-badge-warning` "Sin fecha de actualización de ofertas" |
| official `offers` empty | muted empty state inside the section; the refresh button stays available |
| "Actualizar ofertas" in flight | button `loading` (own state, page does not re-enter its loading state) |
| refresh failure | inline `Alert variant="danger"`; the already loaded game and its offers stay on screen |
| timestamps (`observedAt`, `lowestPriceAt`, `offersRefreshedAt`) that are not valid ISO date-time strings | normalized to `null` and rendered as "—" / "Sin fecha"; the date formatter is guarded so it can never throw |
| offer comparable in MXN (`mxnCurrentPriceMinor` present **and** `pricingType !== "unconverted"`) | eligible for the cheapest tally and for the summary's `Mejor precio comparable`; original currencies are never compared across rows |
| one or more comparable official offers tie at the lowest `mxnCurrentPriceMinor` | **every** tied row carries `tabler-badge-success` "Más barato" (with an `sr-only` " entre las tiendas comparadas en MXN") and its `Precio` and `Aprox. MXN` cells switch to `.text-success`. The row background is never set, so `.table-row:hover` keeps working |
| cheapest offer is also `originalCurrentPriceMinor === 0` | `Precio` stays "Gratis" (already `.text-success`); the badge and the MXN cell still mark the win |
| `drmNames` non-empty | up to two `tabler-badge-info` badges after the store name; the remainder becomes a `+N` info badge whose hidden text lists the names, behind an `sr-only` "DRM:" legend |
| `platformNames` non-empty | same shape in `tabler-badge-muted`, legend "Plataformas:" |
| `drmNames` / `platformNames` empty | nothing renders — no placeholder, no empty badge |

Best-price strip inside the `app-card-accent` summary (same file):

| Condition | Treatment |
|---|---|
| candidates | the direct Steam price (only when `game.currency` is `MXN` **and** `currentPriceMinor` is present) plus every comparable official offer |
| comparison basis | `mxnCurrentPriceMinor` only; FX dates are not normalized across rows, so the strip is a snapshot comparison, not a same-day quote |
| tie | Steam wins; between stores, lexical order by `shopName` then `offerKey` (stable across refreshes) |
| winner is Steam | label "Steam · precio directo", link to the existing Steam store URL, price in `.deal-price .text-primary` — never green, since the same number is already the big price above |
| winner is an offer, strictly cheaper than the direct Steam price, or Steam has no comparable price | price in `.deal-price .text-success` |
| winner offer is `fx_estimate` | price prefixed `≈` and the rate note (`Tasa <rate> · <dd/MM/yyyy> · <source>`) repeated underneath |
| winner offer price is `0` | price reads "Gratis" |
| winner offer `dealUrl` is not an absolute `https:` URL | the store name renders as plain text, with no link |
| nothing comparable | muted "Sin precio comparable en MXN por ahora."; no row is highlighted |
| always | kicker "Mejor precio comparable" plus the muted line "Compara solo precios en MXN: el precio directo de Steam y las tiendas oficiales de la tabla.", so the summary never claims to cover every store |

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

### Offers section (game detail)

- Sits in an `.app-card` **after** the existing Steam summary and Steam table, whose markup and layout
  are unchanged.
- Header: `uppercase tracking-widest` kicker, `text-xl` heading, the note "Solo tiendas oficiales"
  (v1 renders the official group only), the attribution line **"Datos de precios: IsThereAnyDeal"**
  (required by ITAD's terms, always visible) and the `Button variant="secondary"` "Actualizar ofertas".
- One `.table-shell` (`overflow-x-auto` + `min-w-max`) for the single official group. Columns are
  fixed: `Tienda | Precio base | Descuento | Precio | Moneda | Aprox. MXN | Observado`. The header block
  stacks below `sm` and the tables scroll horizontally rather than squashing.
- `Observado` uses `Intl.DateTimeFormat("es-MX", { dateStyle: "medium" })`. `fxRateDate` arrives as
  `YYYY-MM-DD` and is reordered to `dd/MM/yyyy` by string split — never through `new Date()` — so the
  day cannot shift by timezone.
- FX rates render with `Intl.NumberFormat("es-MX", { minimumFractionDigits: 2, maximumFractionDigits: 4 })`.
- Store cell stack: the shop link/name and the "Oficial"/"Más barato" badges on one `flex-wrap` line,
  then one `NameBadges` row for `drmNames` (info tone) and one for `platformNames` (muted tone). Each row
  shows at most two badges plus `+N`; the `+N` pill carries the hidden names and an `sr-only` legend
  ("DRM:" / "Plataformas:") so the pills are never unlabelled words. Nothing renders for empty arrays.
- The summaries' `Mejor precio comparable` strip sits inside the existing `app-card-accent`, below the
  Steam badge cluster and above "Ver en Steam", separated with `border-t border-default pt-3`. It reuses
  the same cross-store comparison as the table (`selectBestPrice` / `cheapestTies` in `game-client.tsx`),
  `.deal-price` for the number and the standard external-link pattern for the winning store.

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
- **External deal link (offers):** the shop name is the link, using `dealUrl` **verbatim** (ITAD's
  affiliate tag must never be stripped or rewritten), `target="_blank"`, `rel="noopener noreferrer"`,
  `ExternalLink` icon `aria-hidden` and the same `sr-only` "(se abre en una pestaña nueva)" note. Only
  an absolute `https:` URL is accepted by the normalizer; anything else (missing, relative, `http:`,
  another scheme, or unparseable) becomes `null` and the shop name renders as plain text.
- **Refresh (offers):** "Actualizar ofertas" calls `refreshSteamGame` (`app/steam/_lib/steam-api.ts`),
  a `POST` to `/api/bff/steam/games/<appId>` through `csrfFetch`. A dedicated `refreshing` flag drives
  only the button's `loading`/`aria-busy`, so the page never returns to the full-page "Cargando..."
  state and the tables stay readable during the call. An `aria-live` `polite` status line shows
  "Consultando tiendas..." while it runs.
- **Refresh failure (offers):** inline `Alert variant="danger"` above the tables; the previously loaded
  game and offers stay on screen. A failed refresh never blanks the detail page.
- **Cheapest highlight (offers):** the highlight is text-only — a `tabler-badge-success` "Más barato"
  plus `.text-success` on the `Precio` and `Aprox. MXN` cells. No background or border is added to the
  `<tr>`, so `.table-row:hover` (a `@layer components` rule) keeps working; a utility background would
  outrank it and kill the hover feedback.
- **Best price strip:** static content, no interaction beyond the winning store's external link. When
  nothing is comparable it degrades to a muted line; it never renders a zero, a dash inside a price
  class, or a green claim without a comparable number behind it.
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
- Offers section: one `sr-only` `caption` for the official group table ("Ofertas de <juego> en
  <grupo>") plus `th scope="col"`; the group heading is a real `h3` tied to its table region with
  `aria-labelledby`. The refresh control announces its own state (`aria-busy` + live region), and the
  stale/refresh-date warnings are text badges rather than a color change alone.
- Offer state is always carried by text ("Precio actual", "Datos incompletos", "Sin fecha de
  actualización") in addition to color.
- "Más barato" is a visible badge, so the cheapest rows are not signalled by green text alone; its
  `sr-only` tail ("entre las tiendas comparadas en MXN") scopes the claim for screen readers. DRM and
  platform badges sit behind `sr-only` legends, and the names hidden by `+N` are read out in full.
- The `Mejor precio comparable` strip names its comparison basis in visible text, so the summary is
  never understood as covering every store or every offer.
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
- **Not in scope:** keyshops and grey market (ITAD does not cover them; that would need gg.deals or
  another provider), price history charts, alerts/watchlists, currency switching, bundles, and
  historical FX — only the day's rate is used. **Authorized stores are deferred in v1:** the contract
  keeps `classification` (`official | authorized`) so the second group can come back without a model
  change, but the UI renders official stores only. Copy must not claim "all stores", must not use the
  word "keys", and must not imply grey-market coverage. The local low remains a single stored datum,
  not a history chart.
- **External data is untrusted.** `lib/contracts/steam.ts` is the trust boundary: offers with an
  unknown/missing `pricingType` or a missing required field are dropped; non-`https` deal links and
  non-ISO timestamps become `null` and render as plain text/"—". Nothing from the provider is rendered
  unvalidated, and invalid values never throw in the render path.
- **Offers are a snapshot, not history.** `game_offers` is upserted per (game, source, offer key) and the
  UI only shows the last observed values. `offersStale` means the latest refresh failed and persisted
  data is being shown — not that the price changed.
- **Offers ordering.** Rows render in API order, filtered to `classification === "official"`. No
  client-side sorting, shop filter or preference; add them only against a real requirement, since the
  server owns ordering.
- **Attribution is a ToS requirement.** "Datos de precios: IsThereAnyDeal" and the untouched `dealUrl`
  are not decorative: removing either breaks ITAD's terms. Links are validated as absolute `https:`,
  which is a safety check, not a rewrite — the URL string itself is passed through unchanged.
- **Comparison basis.** Only `mxnCurrentPriceMinor` is compared; original currencies are never compared
  across stores, and each MXN value carries the FX snapshot of its own row (dates are not aligned). Both
  the `Más barato` badge and the `Mejor precio comparable` strip therefore describe the stored snapshot,
  not a same-day quote — hence the explicit labels in each place.
- **Highlight vs. strip can differ.** The badge marks the cheapest **official offer row**; the strip also
  considers the direct Steam price. When Steam is cheaper, no row is highlighted and the strip says so.
  Copy must keep the two claims distinguishable.
- **DRM and platform names come from ITAD** (`drmNames` / `platformNames`) and may be empty or unknown
  strings: empty arrays render nothing, unknown names render verbatim, and there is no expand/collapse UI
  — the `+N` remainder is only exposed to assistive tech. Add a popover only against a real need.
- **No unit tests.** `selectBestPrice` / `cheapestTies` are pure functions on purpose so they can be
  covered the day a test runner exists; this repo has none and adding one is out of scope here.
- **Verification.** No test project exists in this repo, so the check is `pnpm build` plus manual
  browser QA (owed: home/search/detail at light and dark, mobile drawer keyboard walkthrough, and the
  offers section with: tied cheapest rows, a `fx_estimate` offer, an `unconverted` offer, DRM/platform
  badges with more than two names, the stale badge, an empty result and a failed refresh). No Lighthouse
  or visual-regression run was executed.
