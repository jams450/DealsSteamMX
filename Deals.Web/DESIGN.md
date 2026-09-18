# Deals Steam MX Web — Design Contract

Single source of truth for tokens and primitives used by the Deals Steam MX product UI. Every color,
spacing value, radius and state in a component must trace back to a token defined here or in
`app/globals.css`. When a new value is needed, add the token (here + `globals.css`) **before**
using it. Do not hardcode hex/`rgb()` or arbitrary pixel offsets in components.

Tailwind v4 is configured in CSS (`@import "tailwindcss"` in `app/globals.css`); there is no
`tailwind.config.*`. Tokens are CSS custom properties consumed through semantic utility classes
(`.text-*`, `.app-card`, `.input-semantic`, `.table-*`, `.tabler-badge-*`).

## 0. Source analysis (extracted, not invented)

- Token layer: `app/globals.css` (`:root`, `.dark`, `[data-theme="blue|light-blue"]`).
- Primitives: `components/ui/{button,input,card,alert,select,price-value}.tsx`, `components/data-grid/data-grid.tsx`,
  `components/theme/theme-toggle.tsx`, `components/brand/logo.tsx`, `lib/ui/cn.ts`,
  `lib/bff/http.ts` (typed errors).
- Shells: `components/navigation/product-shell.tsx` (product surface) and
  `components/navigation/admin-shell.tsx` (admin surface, despite the file name — it is imported by
  `/users` and `/steam`). Both now draw on the same tokens, `.app-card`/`.app-topbar`, radii and shadows.
- Patterns: semantic classes (`btn-*-semantic`, `input-semantic`, `table-*`, `app-card`,
  `tabler-badge-*`) instead of raw Tailwind palette utilities.
- Theme starts dark by default (`app/layout.tsx` init script, `localStorage.theme`), so dark values
  are the primary surface and are tuned as such.

## 1. Product scope and routes

Deals Steam MX is a game-deal finder: search a game, open its detail page, read the price, the discount and
when it was last observed.

| Route | Files | Surface | Notes |
|---|---|---|---|
| `/` | `app/page.tsx` | Product | Home: search as the central CTA, how-it-works, price source note |
| `/search` | `app/search/page.tsx`, `app/search/search-client.tsx` | Product | Text search; `?q=` pre-runs the query; local suggestions while typing (≥2 chars) |
| `/games/[steamAppId]` | `app/games/[steamAppId]/{page,game-client}.tsx` | Product | Offer detail: cover, Steam price block, ownership line (`ownership`), local-low row, Steam source table, multi-store offers grouped by provider (ITAD / gg.deals) |
| `/wishlist` | `app/wishlist/{page,wishlist-client}.tsx`, `app/wishlist/_lib/*` | Product | Steam wishlist: four explicit states, "Sincronizar ahora" with its report, a price-reference table (base, historical low, MXN official/keyshop minimums) with a per-row refresh, per-band discount % and a hybrid 0-10 deal score driven by a backend viable-minimum threshold |
| `/library` | `app/library/{page,library-client}.tsx`, `app/library/_lib/*` | Admin | Playnite library import (manual JSON upload of ≤10 MiB) plus the owned/subscription list grouped by store, with the Game Pass tag and the explicit "Sin precios vinculados" state |
| `/login` | `app/login/page.tsx` | Public | Only public page, plus `/api/auth/{login,refresh,session}` |
| `/users` | `app/users/*` | Admin | Reference admin slice (`AdminShell` + `DataGrid`), admin role only; consumes the same tokens, cards, badges and `Button` variants as the product surface |
| `/steam` | `app/steam/*` | Product (legacy) | Earlier Steam page rendered inside `AdminShell`; not part of `ProductShell` |

- **Gate:** `middleware.ts` + `isPublicRoute` (`lib/security/route-policy.ts`) redirect any app route
  without a usable session to `/login?reason=session_expired`. There is currently no public product
  surface; "product" means the consumer-facing area (`/`, `/search`, `/games/*`), not anonymous access.
- **Scope of this contract:** `/`, `/search`, `/games/[steamAppId]`, `/wishlist`, `/library` and `ProductShell`. `/users`
  also consumes it (tokens, `.app-card`, `.app-topbar`, `.table-*`, semantic badges and `Button` variants)
  while keeping `AdminShell` and its admin-only gating. `/steam` still renders inside `AdminShell` with its
  previous layout: it inherits the shared tokens but was not migrated.
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
- **Wishlist reality:** `/wishlist` is fed by the API's `GET /api/wishlist` (BFF `GET /api/bff/wishlist`)
  and `POST /api/wishlist/sync` (BFF `POST /api/bff/wishlist/sync`). The list is a local snapshot imported
  from Steam's wishlist service (appid, priority, added date, cover) and enriched with ITAD's canonical
  game id; it is not a live Steam call per render. Sync is explicit and manual — the page never polls — and
  it does two things in one request: it imports the list, and it pulls a Steam snapshot for every wished
  game that had no local row yet (that second part is why its report has its own counters). The
  API answers with one of four states (`ok | inaccessible | no_steam_id | never_synced`) and the UI renders
  that state verbatim: it never infers "empty" from a private wishlist, because Steam returns the same
  empty payload for both. Each item also carries a price snapshot (`basePriceMinor`/`baseCurrency`,
  `historyLowMinor`/`historyLowCurrency`, `bestOfficialMinor`, `bestKeyshopMinor`) shown as a reference
  beside the wishlist data, plus a per-row refresh that reuses the game detail's refresh endpoint. No price
  from this page enters the comparator, `selectBestPrice` or any savings math. The response's
  `minViableDiscountPercent` (0..95, default 50) is a per-user preference saved with
  `PUT /api/wishlist/preferences` (BFF `PUT /api/bff/wishlist/preferences`) and only feeds the two deal
  scores. The list uses the shared `DataGrid` in client mode for sorting, global filtering, per-column
  filtering and pagination; mobile tiles consume the same name/AppID filter state.
- **Library reality:** `/library` is fed by the API's `GET /api/library` (BFF `GET /api/bff/library`) and
  `POST /api/library/import` (BFF `POST /api/bff/library/import`), both `AdminWithId` on the API. The list is
  the `user_library` snapshot imported by hand from a Playnite JSON export: the read answers `{ items: [...] }`
  and the import answers `imported`, `updated`, `unresolved`, `unsupportedSource` and `byStore`. It is not a
  per-store integration and it never reads the user's machine. Each item also carries the backend-computed
  price binding: `priceState` (`exact | title_candidate | none | subscription`), `bindingSource`
  (`steam | itad | title | null`), `steamAppId`, `bestOfficialMinor`/`bestKeyshopMinor` (already MXN minor
  units), `historyLowMinor`/`basePriceMinor` (provider currency) and `baseCurrency`. There is **no FX in the
  browser**: MXN is rendered as-is and the provider currency is shown as its code via `Intl`, never converted.
  A subscription row (`state === "subscription"`) carries the filled Game Pass tag with no price and no
  ownership language, and `priceState === "title_candidate"` always shows the fixed label
  "Precio vinculado por título". No price from this page enters the comparator, `selectBestPrice` or any
  savings math. `storeGameId`, `bindingSource` and `steamAppId` are identity data and are never rendered.

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
| Accent | Deals Steam MX brand and actions | `--color-accent` | `.text-accent`, `.btn-primary-semantic`, `.border-accent` |

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
  package is installed and no new dependency is allowed, so the Deals Steam MX voice comes from tracking,
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

- Owns the product surface: sticky translucent header (`.app-topbar`), brand mark + `Deals Steam MX`
  wordmark, desktop navigation, header search, page title/subtitle, and the mobile drawer.
- Props are unchanged: `title`, `subtitle`, `meta`, `children`.
- Header search is a native `role="search"` form posting `GET /search?q=`. It is hidden below `lg`;
  the drawer renders the same form (id `shell-search-mobile`) for mobile.
- Active route: `data-active` styling is not used; the active link gets
  `.border-accent` + `.bg-[var(--color-accent-soft)]` and `aria-current="page"`.
- Mobile drawer: full-height `app-sidebar` panel, scrim, `role="dialog" aria-modal="true"`, focus
  trap, `Escape` to close, focus returned to the trigger, body scroll lock, and `overflow-y-auto`
  so the search + navigation + theme toggle always fit.

### Brand lockup (`components/brand/logo.tsx`)

- Single source of the identity: `LogoMark` (mark only) and `Logo` (mark + wordmark). No other component
  inlines the mark, and no brand SVG lives outside this file and `app/icon.svg`.
- **Mark:** an accent chip whose negative space is a funnel — a wide upper chevron (many stores)
  narrowing into a narrow lower one (one lowest price). One `path` with `fillRule="evenodd"` and
  `fill="currentColor"`, so the cut-outs are real transparency (no `mask`/`clip` ids, safe to mount
  several times per page), the tone comes from the surrounding text color, and the chip reads against
  whatever surface sits behind it in either theme. Geometry is authored in a 32x32 box with a
  ~2.3px minimum feature at 16px. Decorative: `aria-hidden="true"`, `focusable="false"`; size is
  supplied per surface through `markClassName` (32px header, 36px admin sidebar and login, 28px and
  32px drawers).
- **Wordmark:** the three words are stacked — `Deals` (`text-sm font-bold tracking-tight text-primary`)
  over `Steam MX` (`text-xs font-semibold uppercase tracking-[0.14em] text-muted`). Two lines are
  *narrower* than one, which is what keeps the header from crowding at 360px; the string is never
  truncated or abbreviated. The visible stack is `aria-hidden` and an `sr-only` span carries the exact
  product string, so the brand link's accessible name is always `Deals Steam MX`, announced once.
- `app/icon.svg` repeats the mark path with an explicit `#3b82f6` fill, because a standalone SVG file
  cannot read the page's CSS variables. Keep both path strings byte-identical when the mark changes.
- Metadata belongs to `app/layout.tsx`: `metadata` (`title`, `description`, `icons`, `openGraph`,
  `twitter`) plus the `viewport.themeColor` pair that mirrors `--tabler-page-bg` in dark and light.
  There is no `title.template` and no per-page metadata export — the shells own the visible page titles.

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

### Posesión en el detalle (`game-client.tsx`)

- One badge line under the `AppID` line of the summary header, before the price block. It is rendered from
  the `ownership` object the detail payload already carries (`components/`-free: three badges, no fetch, no
  new route, no loading state). When all three fields are empty — and when `ownership` is absent in an old
  response — the line does not exist at all: no empty container, no placeholder, no extra space.
- The store display names come from `lib/contracts/stores.ts`, the same map the `/library` filter and report
  consume. Store keys are canonical (`steam | epic | gog | xbox | amazon | ubisoft | humble | battlenet`,
  with the known grafía aliases accepted and mapped); anything else, and `steam` itself, is dropped by the
  normalizer because this page **is** Steam and that badge would be a duplicate of the page being viewed.
- The normalizer (`normalizeOwnership` in `lib/contracts/steam.ts`) deduplicates, caps at 16 stores, and only
  accepts a literal `true` for `hasGamePass`. An absent, malformed or non-object `ownership` is "nothing to
  show" and can never gate the page.

| Condition | Treatment |
|---|---|
| `ownedStores` non-empty | one `tabler-badge-info` per store: "Ya lo tienes en <tienda>" using the store display names. Confirmation of identity, so it states the store and nothing else |
| `hasGamePass === true` | only the standalone `tabler-badge-solid tabler-badge-primary` "Game Pass" tag — the same treatment as `/library`'s rows. It never merges into the "Ya lo tienes" wording, and when a Steam-owned copy also exists in Game Pass both badges render separately |
| `possibleMatchStores` non-empty | `tabler-badge-warning` "Posible coincidencia en <tiendas>" — the same warning tone as the biblioteca's "Precio vinculado por título", so a tentative state never reads as a confirmation |
| everything empty or the property absent | nothing renders: the line does not exist, spacing is unchanged |
| always | no price claim, no discount and no savings math from ownership; ownership never feeds the comparator or `selectBestPrice` |

### Wishlist (`/wishlist`)

- Rendered inside `ProductShell` (`title="Wishlist de Steam"`). The page is a server component that
  redirects to `/login` without a session and delegates every state to `wishlist-client.tsx`; the route is
  protected by the global matcher in `middleware.ts` (`lib/security/route-policy.ts` is untouched).
- Two stacked zones: an `app-card-accent` summary (kicker "Sincronización", `text-xl` heading "Wishlist de
  Steam", the sync control, the state badges and the sync report) and one `app-card` items section.
- Summary badges: item count (`tabler-badge-muted`, only in state `ok`) plus "Última sincronización
  <fecha>" (`tabler-badge-info`) or "Sin fecha de sincronización" (`tabler-badge-warning`). Tone is never
  the only signal: every badge carries its words.
- Items: a `ul` of bordered tiles below `md` and a `.table-shell` table from `md` up
  (`Portada | Juego | Precio base | % dto. oficial | % dto. keys | Deal oficial | Deal keys |
  Mínimo histórico | Mín. oficial | Mín. keys | Prioridad | Alta | Actualizado | Acciones`), so the row
  works at 360px and the desktop table is never squashed. `Prioridad` stays in the column menu but is
  hidden by default (the user does not use Steam's rank) and it is not part of the mobile meta line. The game
  name is an internal `Link` to `/games/<appId>`; the cover is the local `WishlistThumb` (90×34 below `sm`,
  120×45 above, decorative `alt=""`, `Gamepad2` placeholder when the URL is missing or fails). The ITAD
  identity is a badge ("Identificado en ITAD" info / "Sin identificar en ITAD" muted) in the row, never a
  bare id. On the tiles the four price columns become a two-column `Precio base / Mínimo histórico /
  Mín. oficial / Mín. keys` grid above the meta line, and the four new metrics (`% dto. oficial`,
  `% dto. keys`, `Deal oficial`, `Deal keys`) join the same grid so mobile matches desktop.
- **Money is shown in the currency it arrives in, never converted.** `Precio base` and
  `Mínimo histórico` use `Intl.NumberFormat("es-MX", { style: "currency", currency })` (via
  `lib/format/currency.ts`, the same helper the game detail uses), so a non-MXN code prints with its own
  symbol or code ("US$12.34", "XYZ 1,234.00") and can never be read as MX$. `Mín. oficial` and `Mín. keys`
  are MXN because the API already converted them; they are not marked as approximate and the section note
  says so. An amount without its currency (or the reverse) reads "—": the pair is never half-rendered and no
  currency is assumed. A `0` prints as a zero amount, never as "Gratis".
- **Per-row "Sincronizar"** reuses the existing detail refresh (`refreshSteamGame(appId)` →
  `POST /api/bff/steam/games/<appId>`, which updates Steam, ITAD and gg.deals in one call). It is the only
  per-row action, all 10 columns above include it, and it is a real `Button` (`variant="secondary"`,
  `type="button"`) with an `aria-label` naming the game and its AppID.
- Dates use `Intl.DateTimeFormat("es-MX", { dateStyle: "medium" })` through a guarded formatter, so an
  invalid value renders "—" instead of throwing.
- **Discount and deal score:** `% dto. oficial` / `% dto. keys` are `discountPercent(basePriceMinor,
  baseCurrency, best<Official|Keyshop>Minor)` from `app/wishlist/_lib/wishlist-metrics.ts` (pure module,
  no imports): `null` unless the base price is MXN (the store minimums always are), rounded to one
  decimal, printed as "-42.5%" and as "+3.0%" when the store price is above the base. `Deal oficial` /
  `Deal keys` are the hybrid 0-10 `dealScore` (7 points for the discount scale against the viable minimum,
  3 for proximity to the historic low), printed through the `tabler-badge-*` tone band
  (`muted < 4`, `info < 7`, `success >= 7`); the number is always visible, tone is never the only signal.
  The formula and its exported weights (`DISCOUNT_WEIGHT`, `LOW_WEIGHT`, `SCORE_MAX`, `SCORE_CEILING`)
  live in that module and are covered by `wishlist-metrics.test.ts` (`node --test`).
- **Viable minimum:** the toolbar's `Descuento mínimo viable %` numeric input (0-95, labelled) edits the
  backend preference: it commits on `blur` or `Enter` through `updateWishlistPreferences` →
  `PUT /api/bff/wishlist/preferences`, the local state only takes the value the server confirms, and a
  failure reverts the field to the current value and shows an inline `role="alert"` line. It changes both
  deal scores immediately. Only visible columns, page size and density/sorting are local; this threshold
  is not.
- **Filter and sorting:** desktop delegates to the shared `DataGrid` in client mode, with its own global
  search input (`Buscar por nombre o AppID`), a per-column filter row and `getFilteredRowModel`/
  `getSortedRowModel`. The custom global filter matches the game name or AppID text; the column filters use
  the module default `includesString`. The page sizes are 10/25/50/100 plus `Todos`, and the chosen size and
  the visible columns persist in `localStorage` (`wishlist.pageSize.v1`, `wishlist.columns.v1`). Mobile uses
  a compact native input tied to the same global filter state before mapping its tiles, so filtering never
  disappears at 360px. There is no server-side filter, pagination or reordering.

- **Sortable columns:** `Juego` (alphabetical), `Prioridad`, `Alta`, `Actualizado`, `Precio base`,
  `% dto. oficial`, `% dto. keys`, `Deal oficial`, `Deal keys`, `Mínimo histórico`, `Mín. oficial` and
  `Mín. keys`. `Portada` and `Acciones` are not sortable. Numeric and date values sort by their raw
  number/timestamp, not their formatted label.
- **Null sorting:** the column comparator explicitly places `null` values last in both ascending and
  descending directions. A missing price is never coerced to zero, so it cannot appear as the cheapest row.
- The list never renders raw JSON, upstream error bodies, provider ids beyond `appId`, or filesystem paths.

| Condition | Treatment |
|---|---|
| first load in flight | `app-card p-5 text-sm text-muted` "Cargando...", same size as the resolved content |
| fetch failure | `Alert variant="danger"` with the message plus a `Button variant="secondary"` "Reintentar" that re-runs the load |
| `state === "no_steam_id"` | info `Alert`: "Falta configurar tu SteamID64." + explanation and a `.btn-secondary-semantic` link to `/users`. The sync button is **not rendered**: the import cannot work without the id |
| `state === "never_synced"` | info `Alert`: "Aún no hay ninguna sincronización." + "Usa «Sincronizar ahora» para traerla." |
| `state === "inaccessible"` | info `Alert` that says literally "La wishlist es privada o el perfil no es accesible." and explains that the profile **and** the wishlist must be public. Never rendered as "0 juegos" |
| `state === "ok"` with 0 items | `app-card` empty state "Tu wishlist de Steam está vacía." plus the hint to add games in Steam and sync again |
| `state === "ok"` with items | the items section renders and the count badge appears in the summary |
| no filter text | all items render in both desktop grid and mobile tiles |
| filter text | desktop `DataGrid` and mobile tiles match by name or AppID, case-insensitively |
| non-empty wishlist but no filter matches | `DataGrid` and mobile area say "Ningún juego coincide con la búsqueda."; this is not presented as an empty wishlist |
| items present in a non-`ok` state | the items section still renders below the notice: the notice explains the state, the rows are real data |
| item field missing/invalid (`imageUrl`, `priority`, `addedAt`, `refreshedAt`, `itadGameId`) | that cell reads "—" / the muted ITAD badge; the row stays |
| `basePriceMinor` + `baseCurrency` present | `Precio base` in that currency, unconverted and unprefixed; a non-MXN code looks different from MX$ by construction |
| `historyLowMinor` + `historyLowCurrency` present | `Mínimo histórico` in the provider currency, same formatter |
| `bestOfficialMinor` / `bestKeyshopMinor` present | `Mín. oficial` / `Mín. keys` as MXN (`MX$…`), no `≈` and no note: the API sends them already converted |
| base price not MXN, or best price missing | `% dto.` and `Deal` read "—" in muted tone: a percentage is never computed across currencies |
| score present | `tabler-badge` tone band (`muted < 4`, `info < 7`, `success >= 7`) with the number `0.0`-`10.0` always written |
| `minViableDiscountPercent` absent or invalid in `GET /api/wishlist` | normalized to `50`; the page never rejects the whole wishlist for it |
| `PUT /api/wishlist/preferences` in flight | the input is disabled and an `sr-only` live region says it is saving; the scores keep the previous threshold |
| `PUT /api/wishlist/preferences` fails | the field returns to the current value and an inline `role="alert"` line shows the message; both scores stay as they were |
| body not an integer 0-95 | the BFF answers 400 `BAD_REQUEST` without calling the API |
| `minViableDiscountPercent` missing in the PUT response | the BFF answers 502 (no default is invented for a saved preference) |
| amount present, currency missing (or the reverse) | the cell reads "—"; the pair is never half-rendered and no currency is assumed |
| amount `0` | prints as a zero amount of its currency, never as "Gratis": these are reference prices, not a current offer |
| item without `appId` or `name` | dropped by the normalizer, never rendered |
| invalid `imageUrl` (not absolute `https:`) | treated as missing and replaced by the placeholder — the value is never used as a `src` |
| row refresh in flight | only that row's button shows `loading`/`aria-busy`; the other rows' buttons are `disabled` so a burst cannot burn the 6-per-minute budget |
| row refresh failure | `role="alert"` `.text-danger` line in an extra table row (`colSpan` across the table) / under the tile; the rest of the page and the previous prices stay untouched |
| row refresh rejected by the rate limiter | "Se alcanzó el límite de refrescos (6 por minuto por IP). Espera un minuto y vuelve a intentar." — a wait instruction, never the bare word "error" |
| row refresh failed for another reason | the upstream message plus the factual limit note, so a rejection always ends in a wait instruction |
| row refresh succeeded | the list is re-fetched with `getWishlist` and re-rendered in place; there is no navigation, so the scroll position is preserved |
| row refresh succeeded but the reload failed | the page-level `Alert variant="danger"` in the summary says the prices were updated and asks for a page reload |
| `state` missing or unknown | the whole response is rejected at the BFF (502 upstream error), never mapped to a guess |
| a sync report count missing or invalid | the report is rejected at the BFF; no count is defaulted to 0 |
| always | one `sr-only` `<caption>` on the table, `th scope="col"`, decorative covers with `alt=""` and the item name as adjacent text |

### Biblioteca (`/library`)

- Rendered inside `ProductShell` (`wide`, `title="Biblioteca de juegos"`). The server page calls
  `requireAdminSession()`, so the route is admin-only like `/users`; the global matcher already protects it.
- Two zones: an `app-card-accent` import card (kicker "Importación", the file input plus "Importar", and the
  report) and one `app-card` items card (counts, store filter, list). Both BFF routes are `AdminWithId`.
- **Import:** `<input type="file" accept=".json,application/json">` capped at 10 MiB
  (`LIBRARY_IMPORT_MAX_BYTES`) and checked in the browser first (valid JSON, non-empty array at the root)
  before the `POST`. The BFF re-checks the declared length and the real byte length, re-validates the root
  shape, and forwards the array untouched. The file is never stored and never re-read.
- **Filter and list:** one native labelled `<select>` (`Todas las tiendas (N)`, then each store with its
  count), and 100 rows at a time behind "Mostrar N más". Each row shows the title, the store, the state tag,
  "Instalado" when the backend says so, `Alta <fecha>` (or "Sin fecha de alta"), and the price block.
- **Prices (`priceState`):** the row's right column renders `Alta` first and the price block under it, so the
  row keeps its two-line shape at 100 rows per view. `exact` and `title_candidate` render up to four values —
  `Oficial` and `Keys` are already MXN minor units (the backend converted them), `Base` and `Mín. histórico`
  come in `baseCurrency` and are **never** converted in the browser. The money primitive is the shared
  `components/ui/price-value.tsx` (`PriceValue`/`PriceFact`, moved out of `wishlist-client.tsx` unchanged and
  still used by `/wishlist`), so a missing amount reads "—" and no 0 is invented. A field whose amount or
  currency is null is omitted; if all four are missing the row shows one muted "Sin precio" and stays.
  `bindingSource` and `steamAppId` are normalized but never painted.
- **Report:** the four counters as badges plus one muted badge per store. «Sin resolver» and «Fuente no
  soportada» only leave the muted tone when greater than zero, and the note states that a reimport neither
  duplicates nor deletes rows.

**Price/binding states.** The backend computes the binding; the UI only mirrors the four `priceState` values.

| `priceState` | Treatment |
|---|---|
| `subscription` | No price, no discount, no ownership language. The accent Game Pass tag is the only price-adjacent signal, and the normalizer forces this state (nulling every amount) when `state === "subscription"`, so a subscription can never paint a price |
| `exact` | The four MXN/provider values, no extra label: the absent "por título" warning is what marks it as confirmed identity |
| `title_candidate` | The same values **plus** the `tabler-badge-warning` label "Precio vinculado por título". The wording is fixed: it must never read as a confirmed identity |
| `none` (also unknown or absent) | Muted "Sin precios vinculados". A value outside the four is normalized to `none`, never to `exact` |

| Condition | Treatment |
|---|---|
| `state === "subscription"` | `tabler-badge-solid tabler-badge-primary` "Game Pass", the loudest tag of the row. No price line, no ownership copy, and the row keeps no "Sin precios vinculados" note |
| `state === "owned"` | `tabler-badge-muted` "En tu biblioteca" |
| `state === "wished"` | `tabler-badge-info` "Wishlist": the same table holds wishlist rows and they are never shown as owned |
| any other/missing `state` | the item is dropped by the normalizer, never mapped to `owned` |
| every row | no **price-derived** ownership badge of any kind ("Ya lo tienes" and similar belong to a later phase), no discount percentage and no savings math. The pre-existing `state` tags (`En tu biblioteca`, `Wishlist`, `Game Pass`) are the only ownership-adjacent labels and they stay as they were |
| a price field null or invalid | that field is omitted (never a 0, never a convert); at least one valid field keeps the row's price block, and all four missing degrade to "Sin precio" without dropping the row |
| `baseCurrency` invalid | `Base` and `Mín. histórico` are omitted: a malformed code never reaches `Intl.NumberFormat` |
| `isInstalled === true` | `tabler-badge-info` "Instalado" with its own icon; any other value renders nothing |
| `addedAt` present | "Alta <fecha>" via `Intl.DateTimeFormat("es-MX", { dateStyle: "medium", timeZone: "UTC" })` |
| `addedAt` missing/invalid | "Sin fecha de alta"; the row stays |
| `store` | normalized to lowercase (the filter and the counts group by it) and shown through a label map; an unknown store prints its own text |
| store filter with no rows | "No hay juegos de <tienda> en la biblioteca." |
| 0 items | "Tu biblioteca está vacía. Sube el JSON del export de Playnite para llenarla."; the filter is not rendered |
| first load in flight | bordered "Cargando..." block at the resolved size |
| fetch failure | `Alert variant="danger"` plus "Reintentar"; the import card stays usable |
| file rejected before upload | `Alert variant="danger"` with the reason (over 10 MiB, not JSON, root is not an array, empty array); the input is cleared |
| import succeeded | the report renders and the list is re-fetched in place, with no navigation |
| import succeeded but the reload failed | the report stays and an `Alert variant="danger"` asks for a page reload |
| a report counter missing or invalid | the BFF answers 502; no counter is defaulted to 0 |
| `byStore` unusable | the breakdown is omitted; the four counters and the import result still render |
| always | never rendered: raw JSON, the uploaded file, `storeGameId`, BFF routes or credentials |

### Reseñas por plataforma

- Una reseña por `(juego, plataforma)`: `platform` usa el vocabulario de `lib/contracts/stores.ts`, la misma
  llave de `user_library.store`, así que la reseña y la fila de biblioteca se cruzan sin traducción. El
  contrato vive en `lib/contracts/reviews.ts`; los meses viajan como `YYYY-MM` (mapean directo a
  `<input type="month">`), la nota es un entero 0–100 y `scoreLabel` llega calculada por el backend.
- **`scoreLabel` es solo de presentación y la calcula el servidor.** La UI no replica los umbrales de
  `ReviewScoreBands.Label`, no los conoce y no muestra una previsualización de la etiqueta mientras se
  escribe: la etiqueta aparece recién con la respuesta de la API después de guardar. Regla arquitectónica
  explícita.
- **Un solo editor, en `/library`.** Cada fila de biblioteca es un par `(juego, plataforma)`, que es
  exactamente una reseña. La fila muestra la reseña inline (nota + etiqueta, GOTY, rango de meses y texto)
  y permite crear, editar y borrar con los mismos `Button`, badges, `Alert` e `input-semantic` del resto de
  la biblioteca; el formulario usa `<input type="month">` para ambos meses, un número 0–100, una casilla
  GOTY y un `textarea`. Al guardar o borrar, la reseña se actualiza en el sitio en todas las filas que
  compartan `(gameId, platform)`; no hay navegación ni recarga de la lista.
- **GOTY es independiente de Game Pass.** El tag `GOTY` (tono success) es un logro de la reseña y nunca se
  mezcla con el tag de suscripción; un juego en Game Pass puede o no ser GOTY.
- **Una fila sin `gameId` no se puede reseñar.** No se oculta ni se ofrece una acción rota: la fila muestra
  el motivo honesto (el catálogo todavía no le da identidad canónica). Lo mismo si su `store` no está en el
  vocabulario de plataformas.
- **El detalle del juego es de solo lectura.** Muestra las reseñas de todas las plataformas del juego
  canónico después de la comparación de precios, en una tarjeta discreta y visualmente secundaria, sin
  duplicar el editor. Si no hay reseñas, la tarjeta no existe.
- **Campos aditivos.** `gameId` y `review` se suman a cada item de `GET /api/library`; `reviews` se suma al
  payload del detalle. Los normalizadores toleran su ausencia (`null` / arreglo vacío) y nunca inventan una
  reseña. La `review` de una fila solo se pinta si su `gameId` y `platform` coinciden con la fila. La nota
  se acepta solo como entero 0–100 y el mes solo como `YYYY-MM`; cualquier otra forma degrada a `null`, y
  `isGoty` solo es verdadero con el literal `true`.

### DataGrid (`components/data-grid/data-grid.tsx`)

Used by `/users` (admin) and `/wishlist` (product). Props are additive-only and the new capabilities are
opt-in: with none of them set the grid behaves exactly as before. Column IDs are a public contract.
Available: `columns, rows, mode, density, allowDensityToggle, densityStorageKey, loading, emptyMessage,
errorMessage, manualSorting, sorting, onSortingChange, manualPagination, pagination,
onPaginationChange, rowCount, initialSorting, pageSizeOptions, allowAllPageSize, pageSizeStorageKey,
toolbar, stickyHeader, stickyActionsColumn, enableGlobalFilter, globalFilterPlaceholder, globalFilterFn,
enableColumnVisibility, columnVisibilityStorageKey, initialColumnVisibility, enableColumnFilters`.

- `allowAllPageSize` appends a `Todos` option that maps to `Number.MAX_SAFE_INTEGER` internally while the
  `<select>` still shows the label; the pager keeps rendering while the all-rows size is active.
- `enableColumnVisibility` adds the labelled `Columnas` trigger (`aria-haspopup`/`aria-expanded`) with a
  checkbox per hideable column, excluding `actions` and any column with `enableHiding: false`; it closes on
  `Escape` (returning focus to the trigger) and on outside click, and persists to
  `columnVisibilityStorageKey` when given. `initialColumnVisibility` applies only when nothing is stored.
- `enableColumnFilters` enables `getFilteredRowModel` (which also serves the global filter) and renders a
  second, non-sticky header row with a labelled `Filtrar` input per filterable column, skipping
  `cover`/`actions`; the default filter fn is `includesString`. The filter row is not sticky on purpose: the
  sticky header (`z-20`, with background) covers it on scroll instead of stacking on top of it.

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
- **Sync (wishlist):** "Sincronizar ahora" posts through `csrfFetch` (`syncWishlist`,
  `app/wishlist/_lib/wishlist-api.ts`). A dedicated `syncing` flag drives only the button's
  `loading`/`aria-busy` and an `aria-live` status line ("Consultando la wishlist de Steam..."), so the list
  already on screen stays readable and the page never returns to its loading state. On success the report
  renders in an `aria-live="polite"` block inside the summary and the list is re-fetched; if the reload
  fails, the report stays and an inline `Alert variant="danger"` says the list could not be refreshed. The
  report carries the eight counters the API returns, including the sync's own Steam pass ("Traídos de
  Steam" always visible — a `0` there means nothing was missing — and "Fallidos de Steam" in danger tone
  only when `> 0`), and one muted line states that the sync does not touch offer prices, which is why
  "Refrescados" and "Fallidos" are always `0` here.
- **Sync failure (wishlist):** inline `Alert variant="danger"`; the previous list, badges and dates stay
  untouched. A failed sync never blanks the page.
- **Refresh per row (wishlist):** "Sincronizar" calls the existing `refreshSteamGame(appId)`
  (`app/steam/_lib/steam-api.ts`) — the same BFF `POST /api/bff/steam/games/<appId>` the game detail uses,
  which refreshes Steam, ITAD and gg.deals in one call. A `refreshingAppId` flag scopes the `loading`
  state to that single row (the page never returns to "Cargando...") and disables the other row buttons
  while a request is in flight, because the endpoint allows 6 refreshes per minute per IP. On success the
  list is re-fetched in place: no navigation happens, so the scroll position is kept. Failure renders in
  the row itself (`role="alert"`), never as a page-level error, and a limiter rejection is worded as a wait
  instruction instead of an error.
- **Import (library):** the file is read and validated in the browser first, then posted as JSON through
  `csrfFetch`, so the existing global CSRF middleware covers it and is not weakened. A dedicated `importing`
  flag drives only the button's `loading` and the `aria-live` status line, and disables the input so the
  chosen file cannot change mid-flight; the list already on screen keeps rendering and the page never
  re-enters its "Cargando..." state. On success the report appears at the top and the list is re-fetched in
  place.
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

- **Brand assets.** Only `app/icon.svg` exists: there is no raster `favicon.ico`, no `apple-icon` and no
  `opengraph-image`, so link previews render text only and older browsers fall back to a default icon.
  The 16px legibility of the mark was asserted from its geometry, not from a human eye on a rendered
  bitmap. Add a raster `apple-icon` only when a touch/home-screen use actually exists.
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
- **Shared tokens.** The dark token retune is global, so `/steam` (which renders inside `AdminShell`) also
  shifts to graphite without a layout migration. `/users` was migrated to this contract (`app-card`/
  `app-topbar`, semantic badges, `Button` variants) inside `AdminShell`; `AdminShell`'s props, drawer,
  focus trap and scroll lock are unchanged, and the `AdminShell`/`ProductShell` duplication remains
  accepted debt.
- **Providers in scope (policy reversal).** An earlier version of this contract excluded keyshops,
  forbade the word "keys" and forbade claiming "all stores". gg.deals is now a first-class second
  provider and its keyshop aggregate is deliberately in scope, so the UI renders those rows and labels
  them "Keyshop". What has not changed: nothing is scraped, no seller name is invented or guessed, and
  copy must not claim "all stores" — the section shows ITAD's per-store offers plus gg.deals' retail and
  keyshop aggregates and nothing else. The free gg.deals tier returns **one aggregate price per bucket
  and never a per-seller identity**, so copy must never name a keyshop seller or imply that one is
  known, and the gg.deals group must never read as store-by-store detail.
  Still out of scope: any provider other than ITAD and gg.deals, per-seller keyshop prices (a gg.deals
  Premium feature), grey-market sourcing details, price history charts, alerts and notifications,
  currency switching, bundles and historical FX — only the day's rate is used. The Steam wishlist
  (`/wishlist`) **is** in scope as an explicit, manual import with its own page; it brings no alerts, no
  Telegram, no scheduled polling and no owned-library cross-store matching. **Bundles are out of scope for
  the currently implemented comparator** (Steam + ITAD + gg.deals/FX) and are deferred to the repo-root
  `PLAN_BUNDLES.md` as a separate future phase; nothing in this contract defines bundle UI, bundle
  comparison, or bundle savings.
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
- **Wishlist has no filter, sort or pagination.** The API returns the whole snapshot and the page renders
  it in one pass; the normalizer (`app/wishlist/_lib/wishlist-contract.ts`) caps a payload at 5000 items and
  drops unknown/extra fields. Add server-side paging plus a title or store filter only against a real
  oversized account, not speculatively.
- **Two wishlist trust boundaries, one shape.** The BFF route and the client share the same normalizer, so
  both reject an invalid `state` and default a malformed item to "dropped" instead of rendering it. The
  client re-validates what the BFF already normalized: cheap, and it keeps a future non-BFF consumer honest.
- **`WishlistThumb` duplicates `SteamThumb`.** Both are local components with the same 120×45 recipe;
  extract one shared component when a third consumer appears. Same `<img>` debt as the cover note above
  (`next.config.ts` has no `images.remotePatterns`).
- **Manual sync only.** The wishlist page never polls and never syncs on load: a snapshot older than the
  last manual run is shown with its own `syncedAt` badge, and "Sin fecha de sincronización" when the API
  reports none. Scheduled refresh is out of scope.
- **Client-side filtering, no pagination.** Wishlist filtering and sorting stay in the browser because
  the expected list is around 600 rows and the API returns the complete snapshot. The shared `DataGrid`
  is currently unpaged in this screen; add server-side filtering/pagination only when list size or measured
  render cost makes this boundary real.
- **The library is unpaged on the server and filtered in the browser.** The API returns the whole
  `user_library` snapshot (~2.6k rows), so `/library` filters by store client-side and reveals 100 rows per
  "Mostrar más". The normalizer (`app/library/_lib/library-contract.ts`) caps a payload at 20000 items and
  drops any item whose `state` it does not know instead of guessing `owned`. The import size check exists in
  the browser (early feedback) and in the BFF (the real 10 MiB limit); the file is never persisted. Add
  server-side paging/filtering only when a real account outgrows this.
- **The first sync of a big wishlist can take minutes, and the page can only wait.** `POST
  /api/wishlist/sync` pulls the games with no local row from Steam **sequentially, inside the HTTP
  request**, with no pacing (the backend carries its own `ponytail:` note about it): an empty database plus
  a 500-game wishlist means ~500 Steam calls on a single request, so "Sincronizar ahora" can sit in
  `loading` far longer than a normal sync and a proxy or browser timeout may cut it off. The UI states the
  truth rather than faking progress: the button stays `loading`, the list already on screen keeps working,
  and if the request dies the §8 error path reports it. Fixing it belongs in the backend (move the fetch
  pass into the background job or pace it like the price pass) — do not paper over it with a client-side
  timeout or an invented progress bar.
- **The per-row refresh cannot tell a `429` from a `503`.** `refreshSteamGame` collapses the HTTP status
  into the BFF message, so the client only ever sees text. The rate-limit wording is therefore recognized by
  matching that message, and the factual note ("6 por minuto por IP: espera un minuto y vuelve a intentar")
  is appended to any other failure too — a rejection by the limiter always ends in a wait instruction, never
  in a bare "error". Give the row its own state only if the helper ever exposes the status.
- **The four price columns are references, not offers.** `basePriceMinor`/`baseCurrency` and
  `historyLow*` come from the game's own snapshot in its own currency; `bestOfficialMinor` and
  `bestKeyshopMinor` are MXN snapshots the backend derived. They are deliberately **not** part of
  `game_offers`, `selectBestPrice` or any savings math, are never compared across rows, and a row with no
  observation shows "—" for all four instead of a stale or invented number.
- **Verification.** No test project exists in this repo, so the check is `pnpm build` plus manual
  browser QA (owed: home/search/detail at light and dark, mobile drawer keyboard walkthrough, and the
  offers section with: the ITAD table and the gg.deals aggregate list side by side, a keyshop bucket
  (badged) next to a `retail` bucket (no badge, and not readable as an error thanks to the group note),
  a game where only one provider has rows, tied cheapest offers inside one group, a `fx_estimate` offer,
  an `unconverted` offer, an offer with and without historical low, DRM/platform
  badges with more than two names, either stale badge, both attribution links, an empty result and a
  failed refresh). No Lighthouse
  or visual-regression run was executed.
