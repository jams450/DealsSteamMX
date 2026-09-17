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
| `/games/[steamAppId]` | `app/games/[steamAppId]/{page,game-client}.tsx` | Product | Offer detail: cover, Steam price block, local-low row, Steam source table, multi-store offers grouped by provider (ITAD / gg.deals) |
| `/login` | `app/login/page.tsx` | Public | Only public page, plus `/api/auth/{login,refresh,session}` |
| `/users` | `app/users/*` | Admin | Reference admin slice (`AdminShell` + `DataGrid`), admin role only |
| `/steam` | `app/steam/*` | Product (legacy) | Earlier Steam page rendered inside `AdminShell`; not part of `ProductShell` |

- **Gate:** `middleware.ts` + `isPublicRoute` (`lib/security/route-policy.ts`) redirect any app route
  without a usable session to `/login?reason=session_expired`. There is currently no public product
  surface; "product" means the consumer-facing area (`/`, `/search`, `/games/*`), not anonymous access.
- **Scope of this contract:** `/`, `/search`, `/games/[steamAppId]` and `ProductShell`. `/users` and
  `/steam` keep `AdminShell` and their current layout.
- **Data reality:** the detail page is fed by three sources. The regional price is Steam
  (`/api/bff/steam/search`, `/api/bff/steam/games/[appId]`, `/api/bff/steam/suggestions`), MXN / Mexico
  region. Cross-store offers ride on the same game payload (`offers`); the explicit refresh uses
  `POST /api/bff/steam/games/[appId]` and refreshes every provider at once (IsThereAnyDeal and
  gg.deals), each with its own freshness gate and stale flag (`offersStale`, `ggDealsStale`). The
  provider-neutral contract keeps `classification` (`official | authorized | keyshop`); the UI groups
  offers **by provider** (`source` → ITAD / gg.deals), never by store type, so ITAD's official stores
  and gg.deals' retail + keyshop aggregate are two independent comparisons. gg.deals' free tier returns
  a single aggregate price per bucket and never a seller identity, so copy never names a keyshop and
  never claims "all stores".
  Each provider returns offers in its own currency (`original*` fields, the source of truth); the
  MXN columns (`mxn*`) are derived from the day's FX rate (Banxico FIX, Frankfurter fallback),
  nullable, and always presented as approximate. `historyLowAllMinor`/`historyLowCurrency` normalize
  each provider's historical low but are not rendered in v1. Offers are a per-provider snapshot, not
  history. The "local low" (`lowestPriceMinor`/`lowestPriceAt`) is the lowest price observed locally in
  the app's own database, not a Steam-provided value, and it is never a discount.

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
| `source === "itad"` | rendered in the `ITAD` group as a table (`OfferGroup`); the heading names the provider, never the store type |
| `source === "ggdeals"` | rendered in the `gg.deals` group as an **aggregate list** (`AggregateOfferList`), one entry per bucket (`retail` and `keyshop`), never a table; each bucket keeps its own provider historical low |
| any other `source` | not rendered: the grouping only knows the two providers, and rows are counted per group |
| `classification === "official"` | the shop cell of the **ITAD table** carries `tabler-badge-info` "Oficial" |
| `classification === "keyshop"` | the entry carries `tabler-badge-muted` "Keyshop" — text plus tone, never tone alone |
| `classification === "authorized"` | no badge anywhere. This is the **expected** state of the gg.deals `retail` bucket (an aggregate of official *and* authorized stores with no per-store identity), so a bucket without a badge is normal, not a gap; the aggregate list's own note says so in text |
| a provider group has no rows | that group's heading, note and rows are not rendered; the other provider still renders |
| both providers empty | muted empty state inside the section; the refresh button stays available |
| `pricingType === "regional"` | `Aprox. MXN` shows the stored MXN snapshot plainly (no `≈`) |
| `pricingType === "fx_estimate"` | `Aprox. MXN` is prefixed `≈` and carries `Tasa <rate> · <dd/MM/yyyy> · <source>` underneath |
| `pricingType === "unconverted"` | `tabler-badge-warning` "Sin conversión" in `Aprox. MXN`; no MXN value is invented |
| `pricingType` missing or unknown | the whole offer is invalid and is dropped (never silently mapped to `unconverted`) |
| any required offer field missing (`source`, `offerKey`, `shopName`, `classification`, `originalCurrency`) | the offer is dropped |
| `mxnCurrentPriceMinor` missing | `Aprox. MXN` reads "—" |
| `originalCurrentPriceMinor === 0` | the price reads "Gratis" in `.text-success` |
| `originalCurrentPriceMinor` missing | the price reads "—" in `.text-muted` |
| aggregated provider (gg.deals) | **no `Precio base` and no `Descuento` column exist in its group at all**: the API returns one current price per bucket and never a base price or a discount percentage, so those columns would be structurally empty forever. The same reason removes the per-store column: the free tier returns one aggregate, not one row per shop |
| `historyLowAllMinor` **and** `historyLowCurrency` present | ITAD table adds a compact `Mínimo histórico` column whose badge says `mínimo histórico ITAD (juego)` because the same game-level low may repeat across shops; gg.deals aggregate entry keeps `Mínimo histórico <precio>` per bucket. Both are provider lows, never discounts |
| `historyLowAllMinor` or `historyLowCurrency` missing or invalid | the ITAD historical cell reads `—`; gg.deals renders no historical badge — no placeholder amount or invented currency |
| `discountPercent` present (ITAD table only) | `tabler-badge-success` `-N%` in `Descuento`; `Precio base` switches to `.deal-price-strike` |
| `dealUrl` present as an absolute `https:` URL | the bucket/store name is the external link described in §8; the URL is used verbatim, affiliate tag included |
| `dealUrl` missing, not a string, not absolute, or not `https:` | the bucket/store name is plain text (the value is ignored, never rewritten) |
| `observedAt` invalid or missing | `Observado` reads "—" (ITAD table) / "Sin fecha de observación" (aggregate list) |
| `offersStale` **or** `ggDealsStale` | `tabler-badge-warning` "Datos posiblemente desactualizados" |
| `offersRefreshedAt` present | `tabler-badge-info` "ITAD actualizado <fecha>" |
| `offersRefreshedAt` missing or invalid while ITAD rows exist | `tabler-badge-warning` "Sin fecha de actualización de ITAD" |
| `ggDealsRefreshedAt` present | `tabler-badge-info` "gg.deals actualizado <fecha>" |
| `ggDealsRefreshedAt` missing or invalid while gg.deals rows exist | `tabler-badge-warning` "Sin fecha de actualización de gg.deals" |
| "Actualizar ofertas" in flight | button `loading` (own state, page does not re-enter its loading state) |
| refresh failure | inline `Alert variant="danger"`; the already loaded game and its offers stay on screen |
| timestamps (`observedAt`, `lowestPriceAt`, `offersRefreshedAt`, `ggDealsRefreshedAt`) that are not valid ISO date-time strings | normalized to `null` and rendered as "—" / "Sin fecha"; the date formatter is guarded so it can never throw |
| offer comparable in MXN (`mxnCurrentPriceMinor` present **and** `pricingType !== "unconverted"`) | eligible for the cheapest tally of its own provider group; original currencies are never compared across rows, and the tally never crosses groups |
| one or more comparable offers of the same provider tie at the lowest `mxnCurrentPriceMinor` | **every** tied row/entry carries `tabler-badge-success` "Más barato" (with an `sr-only` " entre las tiendas comparadas en MXN de este proveedor") and its price cells switch to `.text-success`. The row background is never set, so `.table-row:hover` keeps working |
| cheapest offer is also `originalCurrentPriceMinor === 0` | `Precio` stays "Gratis" (already `.text-success`); the badge and the MXN cell still mark the win |
| `drmNames` non-empty | up to two `tabler-badge-info` badges after the store name; the remainder becomes a `+N` info badge whose hidden text lists the names, behind an `sr-only` "DRM:" legend |
| `platformNames` non-empty | same shape in `tabler-badge-muted`, legend "Plataformas:" |
| `drmNames` / `platformNames` empty | nothing renders — no placeholder, no empty badge |

Best-price strip inside the `app-card-accent` summary (same file). The comparison is **per provider
group**: each group is measured against the direct Steam price on its own, so the strip never ranks an
ITAD store against a gg.deals row.

| Condition | Treatment |
|---|---|
| candidates | the direct Steam price (only when `game.currency` is `MXN` **and** `currentPriceMinor` is present) plus the comparable offers of **that** group |
| comparison basis | `mxnCurrentPriceMinor` only, within one provider; FX dates are not normalized across rows, so the strip is a snapshot comparison, not a same-day quote |
| tie | Steam wins; between stores, lexical order by `shopName` then `offerKey` (stable across refreshes) |
| winner is Steam | label "Steam · precio directo", link to the existing Steam store URL, price in `.deal-price .text-primary` — never green, since the same number is already the big price above. When Steam wins in both groups the row is rendered once |
| winner is an offer | a muted kicker "Mejor de ITAD" / "Mejor de gg.deals" names the group, so the basis of the number is always visible |
| winner offer is strictly cheaper than the direct Steam price, or Steam has no comparable price | price in `.deal-price .text-success` — only for `regional` rows |
| winner offer is `fx_estimate` | never presented as beating a regional price: price stays `.text-primary`, prefixed `≈`, with the rate note (`Tasa <rate> · <dd/MM/yyyy> · <source>`) repeated underneath |
| winner offer price is `0` | price reads "Gratis" |
| winner offer `dealUrl` is not an absolute `https:` URL | the store name renders as plain text, with no link |
| nothing comparable in either group | muted "Sin precio comparable en MXN por ahora."; no row is highlighted |
| always | kicker "Mejor precio comparable" plus the muted line "Compara solo precios en MXN, por proveedor y por separado: el precio directo de Steam y las ofertas comparables de ese mismo proveedor. Una estimación por tipo de cambio no se presenta como mejor que un precio regional.", so the summary never claims to cover every store and never mixes providers |

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
- Game detail cover: the API `imageUrl` inside `aspect-[460/215]`, full width on mobile and inside
  the summary header's `md:col-span-4` grid cell on desktop. Missing or failed image falls back to the
  same aspect-ratio placeholder, so the header never collapses. Both covers keep the plain `<img>` debt
  noted in §11.

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
- Header: `uppercase tracking-widest` kicker "Ofertas por proveedor", `text-xl` heading, the note
  "ITAD y gg.deals. GG.deals publica un agregado por keyshop, nunca el nombre del vendedor.", the
  attribution line **"Datos de precios: IsThereAnyDeal · GG.deals"** (both providers require an active
  hyperlink, always visible) and the `Button variant="secondary"` "Actualizar ofertas".
- **Two groups, two shapes**, rendered in a `space-y-5` stack because the providers publish different
  granularity. There is no "Tiendas oficiales" group any more.
  - `ITAD` (id `offers-itad`) — `OfferGroup`, one `.table-shell` with fixed columns
    `Tienda | Precio base | Descuento | Precio | Moneda | Aprox. MXN | Observado`, because ITAD returns
    **one offer per store**. The header block stacks below `sm` and the table scrolls horizontally
    rather than squashing.
  - `gg.deals` (id `offers-ggdeals`) — `AggregateOfferList`, **no table**. gg.deals returns one
    aggregate per bucket (`retail` / `keyshop`) and never a base price or a discount, so a table would
    carry two columns that are empty for every row, forever. It renders an `.app-card`-level group
    heading, a muted note ("Agregado por grupo de tiendas, no por tienda: «GG.deals» mezcla tiendas
    oficiales y autorizadas sin identificar cuál, y «GG.deals keyshops» es un agregado de keyshops sin
    nombre de vendedor. Por eso no hay columnas de precio base ni de descuento: el proveedor no las
    publica."), an `sr-only` line ("Ofertas de <juego> en <grupo>") and a `ul` inside a
    `rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4` block. Each `li`
    is a bucket: bucket name as the external link/plain text plus "Keyshop" and "Más barato" badges;
    price line with the current price in the provider currency (the formatter already includes the
    currency), the `≈` MXN estimate and its rate note; footer line with the "Mínimo histórico <precio>"
    info badge and the muted observation date. `shopName` already distinguishes `GG.deals` from
    `GG.deals keyshops`, so no per-store column or identifier is added. The list is `ul`-based on purpose:
    a future provider with several rows per store fits the same shape without a new layout.
- `Observado` uses `Intl.DateTimeFormat("es-MX", { dateStyle: "medium" })`. `fxRateDate` arrives as
  `YYYY-MM-DD` and is reordered to `dd/MM/yyyy` by string split — never through `new Date()` — so the
  day cannot shift by timezone.
- FX rates render with `Intl.NumberFormat("es-MX", { minimumFractionDigits: 2, maximumFractionDigits: 4 })`.
- Store cell stack (ITAD table): the shop link/name and the classification badge ("Oficial" info tone,
  "Keyshop" muted tone) plus "Más barato" on one `flex-wrap` line,
  then one `NameBadges` row for `drmNames` (info tone) and one for `platformNames` (muted tone). Each row
  shows at most two badges plus `+N`; the `+N` pill carries the hidden names and an `sr-only` legend
  ("DRM:" / "Plataformas:") so the pills are never unlabelled words. Nothing renders for empty arrays.
- The summary card has one outer `app-card-accent` with three vertical zones. Its technical header is
  a `grid grid-cols-1 gap-5 md:grid-cols-12`: the image is `md:col-span-4` and the technical content
  (`min-w-0 space-y-3`) is `md:col-span-8`. The image keeps its existing aspect ratio, fallback and
  object-cover behavior without fixed desktop dimensions. Below that grid, `Mejor precio comparable`
  and `Referencia histórica` are full-width sibling blocks, so their borders and content span the outer
  card. The card stacks image then technical content then both zones on mobile.
- `Mejor precio comparable` is separated with `border-t border-default pt-4`. Each provider result is
  its own non-clickable `.app-card` mini-card in `grid gap-3 md:grid-cols-2`; one result uses the full
  available grid width. Each mini-card shows provider heading, comparable MXN price, source link/plain
  label, and the existing FX note. `best.better` remains the only green/better rule; `fx_estimate`
  remains visible but never becomes a green claim against a regional price. The existing comparison
  disclaimer remains below the grid. Empty state stays `Sin precio comparable en MXN por ahora.`.
- `Referencia histórica` is a separate `border-t border-default pt-4` zone. When present it shows
  `Menor mínimo disponible: MX$…` or `≈ MX$…`, plus its source/bucket and disclaimer. It is a
  heterogeneous guide, not a universal historical low, and stays neutral rather than green. When absent
  the zone keeps its kicker and places `Ver en Steam` at the bottom. The Steam CTA always remains at the
  bottom of this historical zone.

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
- **External deal link (offers):** the store name (ITAD table) or the bucket name (gg.deals aggregate
  list) is the link, using `dealUrl` **verbatim** (the ITAD
  and gg.deals ToS both forbid stripping or rewriting it, affiliate tag included), `target="_blank"`,
  `rel="noopener noreferrer"`, `ExternalLink` icon `aria-hidden` and the same `sr-only`
  "(se abre en una pestaña nueva)" note. Only
  an absolute `https:` URL is accepted by the normalizer; anything else (missing, relative, `http:`,
  another scheme, or unparseable) becomes `null` and the name renders as plain text.
- **Attribution (offers):** both providers require an **active hyperlink**, not plain text, on every
  screen that shows their data. The offers header renders "Datos de precios: IsThereAnyDeal · GG.deals"
  as two anchors to `https://isthereanydeal.com` and `https://gg.deals`, each with
  `target="_blank"`, `rel="noopener noreferrer"` and the shared `sr-only` note. They are always
  visible, including when a group has no rows, so the credit can never be missing. Copy never names a
  keyshop seller: the free gg.deals tier returns one aggregate per bucket and no seller identity.
- **Refresh (offers):** "Actualizar ofertas" calls `refreshSteamGame` (`app/steam/_lib/steam-api.ts`),
  a `POST` to `/api/bff/steam/games/<appId>` through `csrfFetch`, and refreshes both providers at once.
  A dedicated `refreshing` flag drives only the button's `loading`/`aria-busy`, so the page never
  returns to the full-page "Cargando..." state and the groups stay readable during the call. An
  `aria-live` `polite` status line shows "Consultando tiendas..." while it runs.
- **Refresh failure (offers):** inline `Alert variant="danger"` above the groups; the previously loaded
  game and offers stay on screen. A failed refresh never blanks the detail page, and a failed provider
  never empties the other provider's group.
- **Cheapest highlight (offers):** the highlight is text-only — a `tabler-badge-success` "Más barato"
  plus `.text-success` on the price and the `Aprox. MXN` value (ITAD table cells, gg.deals list entries)
  — and it is scoped **per provider group**;
  the tally never compares an ITAD row against a gg.deals bucket. No background or border is added to the
  `<tr>`/`<li>`, so `.table-row:hover` (a `@layer components` rule) and the list separators keep working;
  a utility background would
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
- Offers section: one `sr-only` label per provider group — a `<caption>` on the ITAD table and an
  `sr-only` paragraph on the gg.deals list, both "Ofertas de <juego> en <grupo>" — plus `th scope="col"`
  in the table; each group heading is a real `h3` tied to its region with
  `aria-labelledby` (the gg.deals list is a `ul`, so its entries are items, not cells). The refresh
  control announces its own state (`aria-busy` + live region), and the
  stale/refresh-date warnings are text badges rather than a color change alone.
- Offer state is always carried by text ("Precio actual", "Datos incompletos", "Sin fecha de
  actualización") in addition to color; the same applies to classification ("Oficial", "Keyshop").
- "Más barato" is a visible badge, so the cheapest rows are not signalled by green text alone; its
  `sr-only` tail ("entre las tiendas comparadas en MXN de este proveedor") scopes the claim for screen
  readers. DRM and
  platform badges sit behind `sr-only` legends, and the names hidden by `+N` are read out in full.
- The `Mejor precio comparable` strip names its comparison basis in visible text, including the
  provider of each winner ("Mejor de ITAD" / "Mejor de gg.deals"), so the summary is
  never understood as covering every store, every offer or both providers at once.
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
- **Providers in scope (policy reversal).** An earlier version of this contract excluded keyshops,
  forbade the word "keys" and forbade claiming "all stores". gg.deals is now a first-class second
  provider and its keyshop aggregate is deliberately in scope, so the UI renders those rows and labels
  them "Keyshop". What has not changed: nothing is scraped, no seller name is invented or guessed, and
  copy must not claim "all stores" — the section shows ITAD's per-store offers plus gg.deals' retail and
  keyshop aggregates and nothing else. The free gg.deals tier returns **one aggregate price per bucket
  and never a per-seller identity**, so copy must never name a keyshop seller or imply that one is
  known, and the gg.deals group must never read as store-by-store detail.
  Still out of scope: any provider other than ITAD and gg.deals, per-seller keyshop prices (a gg.deals
  Premium feature), grey-market sourcing details, price history charts, alerts/watchlists, currency
  switching, bundles and historical FX — only the day's rate is used.
  **The `retail` bucket carries no classification badge:** it is an aggregate of official *and*
  authorized stores with no per-store identity, so the UI must not claim "Oficial" for it; the value
  arrives as `authorized` and the group note explains why no badge appears. The contract still keeps
  `classification` (`official | authorized | keyshop`) so a future per-store provider can label rows
  without a model change. The local low remains a single stored datum, not a history chart.
- **External data is untrusted.** `lib/contracts/steam.ts` is the trust boundary: offers with an
  unknown/missing `pricingType` or a missing required field are dropped; non-`https` deal links and
  non-ISO timestamps become `null` and render as plain text/"—". Nothing from the provider is rendered
  unvalidated, and invalid values never throw in the render path.
- **Offers are a snapshot, not history.** `game_offers` is upserted per (game, source, offer key) and the
  UI only shows the last observed values. `offersStale` / `ggDealsStale` mean that provider's latest
  refresh failed and persisted data is being shown — not that the price changed. Each provider keeps its
  own timestamp and its own stale flag, so one failing source never marks the other as stale.
- **Offers ordering.** Rows render in API order inside their provider group. No
  client-side sorting, shop filter or preference; add them only against a real requirement, since the
  server owns ordering.
- **Attribution is a ToS requirement.** "Datos de precios: IsThereAnyDeal · GG.deals" and the untouched
  `dealUrl` are not decorative: both providers require an active hyperlink on every screen that shows
  their data, and removing it (or rewriting the URL, which carries an affiliate tag) breaks their terms.
  Links are validated as absolute `https:`,
  which is a safety check, not a rewrite — the URL string itself is passed through unchanged.
- **Comparison basis.** Only `mxnCurrentPriceMinor` is compared, always **within one provider group**;
  original currencies are never compared
  across stores, and each MXN value carries the FX snapshot of its own row (dates are not aligned). Both
  the `Más barato` badge and the `Mejor precio comparable` strip therefore describe the stored snapshot,
  not a same-day quote — hence the explicit labels in each place.
- **Highlight vs. strip can differ.** The badge marks the cheapest row of **one provider group**; the
  strip also considers the direct Steam price for that same group. When Steam is cheaper, no row is
  highlighted and the strip says so. Copy must keep the two claims distinguishable.
- **Historical-low semantics differ by source.** Steam's `lowestPriceMinor` / `lowestPriceAt` is only
  a locally observed low in real MXN, never a provider history. ITAD's `historyLow*` is one game-level
  provider low duplicated across shop rows; the table labels it `mínimo histórico ITAD (juego)` so it
  cannot look shop-specific. gg.deals history is per aggregate bucket (`retail` / `keyshop`) and stays
  in each list entry. The summary's `Menor mínimo disponible` chooses the lowest safely comparable
  candidate across these sources; USD is converted only when the offer's historical currency and
  `originalCurrency` are both USD and `fxRate` exists. It is approximate when converted, sources and
  regions differ, and it is a guide — never call it a universal historical low or an all-time low.
- **DRM and platform names come from ITAD** (`drmNames` / `platformNames`); gg.deals returns neither, so
  its rows render no such badges. They may be empty or unknown
  strings: empty arrays render nothing, unknown names render verbatim, and there is no expand/collapse UI
  — the `+N` remainder is only exposed to assistive tech. Add a popover only against a real need.
- **Classification allow-list and validator are the same list.** `STEAM_OFFER_CLASSIFICATIONS` and
  `toClassification` must be extended together: `normalizeOffer` returns `null` when a classification
  does not validate, so a provider-specific value added in only one of the two places drops every row of
  that kind with no build error and no visible failure. That is how `keyshop` could have disappeared.
- **No unit tests.** `selectBestPrice` / `cheapestTies` are pure functions on purpose so they can be
  covered the day a test runner exists; this repo has none and adding one is out of scope here.
- **Verification.** No test project exists in this repo, so the check is `pnpm build` plus manual
  browser QA (owed: home/search/detail at light and dark, mobile drawer keyboard walkthrough, and the
  offers section with: the ITAD table and the gg.deals aggregate list side by side, a keyshop bucket
  (badged) next to a `retail` bucket (no badge, and not readable as an error thanks to the group note),
  a game where only one provider has rows, tied cheapest offers inside one group, a `fx_estimate` offer,
  an `unconverted` offer, an offer with and without historical low, DRM/platform
  badges with more than two names, either stale badge, both attribution links, an empty result and a
  failed refresh). No Lighthouse
  or visual-regression run was executed.
