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
| `/discover` | `app/discover/{page,discover-client}.tsx`, `app/discover/_lib/discover-api.ts` | Product | Discovery: three read-only lists over persisted `steam_games` rows (discount / historic / recent), capsule cards linking to `/games/[steamAppId]` |
| `/games/[steamAppId]` | `app/games/[steamAppId]/{page,game-client}.tsx` | Product | Offer detail: FX reference strip above the card, cover + official Steam price + sources historical minimum + local Steam minimum + the page's single refresh control, per-provider best-price cards with each price's observation date, CTA footer, ownership line (`ownership`), Steam source table, multi-store offers grouped by provider, bundles, reviews |
| `/wishlist` | `app/wishlist/{page,wishlist-client}.tsx`, `app/wishlist/_lib/*` | Product | Steam wishlist: four explicit states, "Sincronizar ahora" with its report, capsule cards (base, historical low, MXN official/keyshop minimums, per-band discount % and a hybrid 0-10 deal score driven by a backend viable-minimum threshold) with a per-row refresh; a multi-game selection (by AppID) with a sticky bar showing the package total under two alternative scenarios (official / keys) |
| `/library` | `app/library/{page,library-client}.tsx`, `app/library/_lib/*` | Admin | Playnite library import (manual JSON upload of ≤10 MiB) plus the owned/subscription list grouped by store, with the Game Pass tag and the explicit "Sin precios vinculados" state |
| `/login` | `app/login/page.tsx` | Public | Only public page, plus `/api/auth/{login,refresh,session}` |
| `/users` | `app/users/*` | Admin | Reference admin slice (`AdminShell` + `DataGrid`), admin role only; consumes the same tokens, cards, badges and `Button` variants as the product surface |
| `/steam` | `app/steam/*` | Product (legacy) | Earlier Steam page rendered inside `AdminShell`; not part of `ProductShell` |

- **Gate:** `middleware.ts` + `isPublicRoute` (`lib/security/route-policy.ts`) redirect any app route
  without a usable session to `/login?reason=session_expired`. There is currently no public product
  surface; "product" means the consumer-facing area (`/`, `/search`, `/discover`, `/games/*`), not anonymous access.
- **Scope of this contract:** `/`, `/search`, `/discover`, `/games/[steamAppId]`, `/wishlist`, `/library` and `ProductShell`. `/users`
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
  never claims "all stores". `/discover` reads the same persisted rows through
  `GET /api/steam/discover` (BFF `GET /api/bff/steam/discover`): three read-only lists
  (`discount | historic | recent`) projected to card fields only, never a live store call.
  Discover cards select the lowest persisted comparable offer through canonical `game_id`
  (`mxnCurrentPriceMinor` plus `pricingType != unconverted`), including keyshop aggregates. If no
  such offer exists, the card explicitly falls back to Steam. `Mejor precio` is primary and
  `Precio base Steam` is the list/base reference; a percentage renders only when that base is MXN
  and supports an honest comparison. `historic` keeps its narrower Steam-local-low definition and
  `recent` remains ordered only by Steam's observation timestamp.
  Each provider returns offers in its own currency (`original*` fields, the source of truth); the
  MXN columns (`mxn*`) are derived from the day's FX rate (Banxico FIX, Frankfurter fallback),
  nullable, and always presented as approximate. `historyLowAllMinor`/`historyLowCurrency` normalize
  each provider's historical low: they are the per-provider `Mínimo histórico` badge in the offer rows,
  and the candidates of the sources historical minimum in the summary (§7). Offers are a per-provider
  snapshot, not
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
  from this page enters the comparator, `bestGroupOffer` or any savings math. The response's
  `minViableDiscountPercent` (0..95, default 50) is a per-user preference saved with
  `PUT /api/wishlist/preferences` (BFF `PUT /api/bff/wishlist/preferences`) and only feeds the two deal
  scores. The list is paged and ordered server-side and rendered as one game-collection card per
  game at every width: a wide cover anchors identity and categories, `Mejor precio` is the purchasing
  signal, and discount/base price form a restrained secondary rail. Refresh, category editing and
  timestamp stay beside the purchase path; historical, per-source and scoring facts remain in native
  `details`, so every fact is available without turning the immediate view into a dashboard. The
  toolbar search input consumes the same name/AppID draft filter state as the filter panel.
- **Wishlist package preview:** the same page supports selecting several games at once (by AppID, never by
  row index) and asking what that set would cost under two **alternative** scenarios: everything bought from
  a legitimate shop, or everything bought from the keyshop aggregate. The two subtotals never add up — there
  is no combined field on purpose — and a game with no price in a scenario is counted in that scenario's
  `missing` count instead of being priced at 0. `POST /api/wishlist/package-preview`
  (BFF `POST /api/bff/wishlist/package-preview`) is read-only and stateless: the client sends only AppIDs,
  never amounts, and the server re-reads the same snapshot the rows read and stamps `pricedAt`. Nothing is
  persisted, no bundle is applied, and no figure from here enters `game_offers` or any savings math.
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
  "Precio vinculado por título". No price from this page enters the comparator, `bestGroupOffer` or any
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
| both providers empty | muted empty state inside the section; the refresh control (in the game summary) stays available |
| `pricingType === "regional"` | `Aprox. MXN` shows the stored MXN snapshot plainly (no `≈`) |
| `pricingType === "fx_estimate"` | `Aprox. MXN` is prefixed `≈`; the rate is **not** repeated per row — it lives once in the page-level FX reference strip (see below) |
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
| "Actualizar ofertas" in flight (the summary's single control) | button `loading` (own state, page does not re-enter its loading state) |
| refresh failure | inline `Alert variant="danger"`; the already loaded game and its offers stay on screen |
| timestamps (`observedAt`, `lowestPriceAt`, `offersRefreshedAt`, `ggDealsRefreshedAt`) that are not valid ISO date-time strings | normalized to `null` and rendered as "—" / "Sin fecha"; the date formatter is guarded so it can never throw |
| offer comparable in MXN (`mxnCurrentPriceMinor` present **and** `pricingType !== "unconverted"`) | eligible for the cheapest tally of its own provider group; original currencies are never compared across rows, and the tally never crosses groups |
| one or more comparable offers of the same provider tie at the lowest `mxnCurrentPriceMinor` | **every** tied row/entry carries `tabler-badge-success` "Más barato" (with an `sr-only` " entre las tiendas comparadas en MXN de este proveedor") and its price cells switch to `.text-success`. The row background is never set, so `.table-row:hover` keeps working |
| cheapest offer is also `originalCurrentPriceMinor === 0` | `Precio` stays "Gratis" (already `.text-success`); the badge and the MXN cell still mark the win |
| `drmNames` non-empty | up to two `tabler-badge-info` badges after the store name; the remainder becomes a `+N` info badge whose hidden text lists the names, behind an `sr-only` "DRM:" legend |
| `platformNames` non-empty | same shape in `tabler-badge-muted`, legend "Plataformas:" |
| `drmNames` / `platformNames` empty | nothing renders — no placeholder, no empty badge |

**FX reference strip** (above the `app-card-accent` summary, same file). The rate is a page-level fact,
not a per-row footnote: it used to be repeated under every converted price, and the same rate belongs to
the whole page.

| Condition | Treatment |
|---|---|
| strip source | every offer in `game.offers` with `pricingType === "fx_estimate"` **and** a non-null `fxRate`; an offer without a rate contributes nothing |
| deduplication | by the tuple `fxRate \| fxRateDate \| fxSource`, first-seen order. One reference is the normal case; a card refreshed across two days can carry two distinct rates, and collapsing them into one "general" rate would misstate the day each price was converted |
| rendering | one line per distinct reference: `USD → MXN · Tasa <rate> · <dd/MM/yyyy> · <source>`, each part omitted when missing (`formatIsoDate`, never `new Date()`) |
| `USD → MXN` is literal | USD→MXN is the only pair this product converts; any other currency arrives as `unconverted` and carries no rate |
| no reference at all | nothing renders: no empty container, no placeholder |
| surface | `.app-card` with compact padding; the kicker `Tipo de cambio de referencia` (`text-xs font-semibold uppercase tracking-widest text-muted`) is the section's labelled heading, and the strip wraps rather than scrolling at 360px |
| always | the strip states in text that prices marked `≈` are USD→MXN conversions with this rate and are **not** the store's regional price |

Best-price cards inside the `app-card-accent` summary (same file). One card per **provider that has
comparable offers**, showing **that provider's** cheapest MXN price. The comparison is per provider
group, so an ITAD store is never ranked against a gg.deals row.

**Steam is never a candidate and never a card here.** Steam's direct price is the page headline and the
referee of the label, not a competing row. That rule exists because painting a store's name over Steam's
price and link happened twice by two different doors — a group with **no** offers inheriting Steam's
price, then a group where **Steam won the comparison**. Price and its label must always come from the
same source.

| Condition | Treatment |
|---|---|
| candidates | the comparable offers of **that** provider group (`bestGroupOffer`, which deliberately excludes Steam); a group with no comparable offer renders no card |
| comparison basis | `mxnCurrentPriceMinor` only, within one provider; FX dates are not normalized across rows, so the strip is a snapshot comparison, not a same-day quote |
| tie | lexical order by `shopName` then `offerKey` (stable across refreshes) |
| number of cards | one per provider with a comparable offer, so the count reflects which stores have data rather than who wins each comparison |
| green | the **lowest price of all cards**, computed across providers; an `fx_estimate` competes for it. Excluding estimates once put the green on a card more expensive than another card on the same screen, which reads as broken arithmetic — the `≈` prefix and the row's classification badge already say where the figure comes from |
| label of the green card | `Más barato que Steam` when it beats Steam's direct MXN price; `El más barato de las tiendas` when it does not, or when Steam has no comparable MXN price |
| a card that is not green | no label at all: the label belongs to the winner |
| price prefix | `≈` when that provider's best offer is an `fx_estimate`; the rate is not repeated here — see the strip above |
| price `0` | price reads "Gratis" |
| deal link | the offer's `dealUrl` only when it is an absolute `https:` URL; otherwise the store name renders as plain text, with no link |
| observation date | every card carries `Observado <fecha>` from **its own** `offer.observedAt` (`formatObserved`), or `Sin fecha de observación` when it is null. Two cards can be observed on different days and the date is never shared between them |
| provider naming | the card's kicker is the provider group's heading, so the basis of the number is always visible |
| bundle mini-card | stays in this same grid, reporting the bundle's own price for the tiers that include this game; that amount is not the game's price and never enters the comparison |
| nothing comparable in any group | muted "Sin precio comparable en MXN por ahora."; no card is highlighted |
| always | kicker "Mejor precio comparable" plus the muted line "Cada tarjeta es el mejor precio en MXN de ese proveedor, y en verde queda el más bajo de todos: …", so the summary never claims to cover every store and never mixes providers |

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
- Game detail cover: the API `imageUrl` in a bounded side column at `lg` (`lg:grid-cols-12`,
  cover `lg:col-span-4`, content `lg:col-span-8`; stacked below `lg`) with the shorter
  `aspect-[16/7]`, `object-cover` and `rounded-[var(--radius-md)]` inside a bordered
  `bg-[var(--color-surface-2)]` frame, so the price panel reads beside the image at desktop widths
  instead of below a full-width banner; at 360px the cover stacks on top, full-width like before.
  Missing or failed image falls back to the same aspect-ratio placeholder, so the header never
  collapses. Both covers keep the plain `<img>` debt noted in §11.

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

- Sits in an `.app-card` **after** the summary card (comparable cards included). The Steam source
  table moved into this section's first tab, so there is no separate Steam table section any more.
- Header: `uppercase tracking-widest` kicker "Ofertas por proveedor", `text-xl` heading "Ofertas y
  bundles por proveedor" and the note "Cada pestaña muestra un proveedor: Steam oficial, tiendas
  directas (Epic, Microsoft), ITAD por tienda y gg.deals como agregado por grupo de tiendas.",
  plus the attribution line **"Datos de precios: IsThereAnyDeal · GG.deals"** (both
  providers require an active hyperlink, always visible).
- The refresh control is **not in this section**. "Actualizar ofertas" lives exactly once in the game
  summary card, in the row that carries the update date (`Button variant="secondary"`,
  `type="button"`, `loading`, `loadingText="Actualizando..."`), together with its own `aria-live`
  status line and the `refreshError` `Alert variant="danger"`. This section keeps its counts, freshness
  badges and empty state; its empty-state copy still names «Actualizar ofertas» because the control still
  exists, one region above.
- **Provider tabs.** Below the comparable cards, the long stacked provider tables are replaced by
  accessible tabs in one `app-card` section (`Ofertas y bundles por proveedor`): one tab per provider
  group **with** data — `Steam oficial` first and always, then Epic, Microsoft, ITAD, gg.deals and
  `Bundles` (display-only) only when they have rows. Tabs use `role=tablist/tab/tabpanel`,
  `aria-selected`, roving `tabIndex` (only the active tab is reachable with `Tab`) and ArrowLeft /
  ArrowRight / Home / End keyboard navigation with focus moved to the newly selected tab, plus
  `focus-visible` rings. Each panel renders that provider's **existing** table/cards unchanged
  (Steam source table, `OfferGroup`, `AggregateOfferList`, bundle cards) with its own disclaimer/note
  lines moved along (the GOG note lives in the ITAD panel, the bundle note and attribution in the
  Bundles panel). Tab labels are Spanish provider names; tabs whose group already counted rows carry
  that count as a badge (Steam's single-row table carries none). The section header (attribution,
  freshness badges, empty state) stays above the tablist, so the ToS credit can never be missing.
  Only local tab state is added; no data logic, helper, fetch, refresh or favorite changes.
  - `ITAD` (id `offers-itad`) — `OfferGroup`, one `.table-shell` with fixed columns
    `Tienda | Precio base | Descuento | Precio | Moneda | Aprox. MXN | Observado | Mínimo histórico`,
    because ITAD returns **one offer per store**. The header block stacks below `sm` and the table
    scrolls horizontally rather than squashing.
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
    currency) and the `≈` MXN estimate; footer line with the "Mínimo histórico <precio>" info badge and
    the muted observation date. `shopName` already distinguishes `GG.deals` from
    `GG.deals keyshops`, so no per-store column or identifier is added. The list is `ul`-based on purpose:
    a future provider with several rows per store fits the same shape without a new layout.
  - Direct stores (`Epic Games Store`, `Microsoft Store`) — `OfferGroup` table with the same columns as
    ITAD: one row per game with the regional price, first among the store tabs because it is the store's
    real price, not an estimate.
  - `Bundles` — the bundle cards moved unchanged from their old section (header, note, attribution,
    counts, freshness badges and cards), display-only: never offer rows, never savings math.
- `Observado` uses `Intl.DateTimeFormat("es-MX", { dateStyle: "medium" })`. `fxRateDate` arrives as
  `YYYY-MM-DD` and is reordered to `dd/MM/yyyy` by string split — never through `new Date()` — so the
  day cannot shift by timezone.
- FX rates render with `Intl.NumberFormat("es-MX", { minimumFractionDigits: 2, maximumFractionDigits: 4 })`.
- Store cell stack (ITAD table): the shop link/name and the classification badge ("Oficial" info tone,
  "Keyshop" muted tone) plus "Más barato" on one `flex-wrap` line,
  then one `NameBadges` row for `drmNames` (info tone) and one for `platformNames` (muted tone). Each row
  shows at most two badges plus `+N`; the `+N` pill carries the hidden names and an `sr-only` legend
  ("DRM:" / "Plataformas:") so the pills are never unlabelled words. Nothing renders for empty arrays.
- Above the accent card, an FX reference strip (documented in §4) renders only when some offer is an
  `fx_estimate` with a rate; otherwise it does not exist at all.
- The summary card is one outer `app-card-accent` with `overflow-hidden`. A `grid gap-5 p-5
  lg:grid-cols-12` block splits it below `lg` into a stacked cover plus content; at `lg` and up the
  cover sits in its own `lg:col-span-4` column and the identity block, price panel and action row
  stack in `lg:col-span-8`, so mobile order matches desktop and no width leaves dead space inside
  the cover frame.
- The page hierarchy is **hero → Mejor precio comparable → Detalles**. The comparable-price section
  is a full-width sibling immediately after the summary card; only provider tabs, tables, bundles
  and their supporting notes live in the later details section.
- The action row spans the full content width. One `flex flex-wrap items-center gap-3` row holds, in
  order: the update-date badge, the page's single `Actualizar ofertas` control, `Ver en Steam`,
  `Buscar en Ubisoft Store`, and the `aria-live` refresh message. `Ver en Steam` is the canonical store
  page link; `Buscar en Ubisoft Store` is a title search only (Ubisoft has no price client and no
  buildable id) and says "buscar" for exactly that reason. The `refreshError` alert renders immediately
  below this row.
- The identity block carries, in this order: the "Precio Steam · México" kicker, the game name, the
  AppID + favourite row and the ownership badges.
- The price panel (`rounded-[var(--radius-md)] border border-default bg-[var(--color-surface-2)] p-4`)
  splits "what you pay today" from "historical context" in a `grid gap-4 sm:grid-cols-2`: the left cell
  holds the **official Steam price** (the `text-3xl` `.deal-price` headline, its `-N%` badge, the "Precio
  actual" / "Sin precio" state badge and "Datos incompletos"); the right cell holds the **sources
  historical minimum** as a visible figure and the **local Steam minimum** badge, divided by the panel
  edge (`sm:border-l`, a horizontal `border-t` below `sm`).
- The sources historical minimum shows the `Mínimo histórico de las fuentes` kicker, then
  `.deal-price text-xl text-primary` prefixed `≈` when the candidate is approximate, then its source
  label (`text-sm font-semibold text-secondary`). It never uses `text-success`: on this page success
  marks the current available price, the discount and the at-low local minimum, never a heterogeneous
  historical figure from mixed sources and regions. It is a different fact from the local Steam
  minimum — one is what our database observed for Steam, the other is the providers' all-time low — so
  the two always carry their own label and are never merged. Steam's local low is a candidate only when
  `game.currency` is MXN: this figure is printed as an MXN amount, so a non-MXN card would print `MX$`
  over a foreign amount, right beside the badge that formats it in its own currency. It is the same
  condition `steamComparablePrice` already applies to the direct price.
- `Mejor precio comparable` is a full-width section immediately after the summary card. One
  non-clickable `.app-card` mini-card per provider that has a comparable offer, in `grid gap-3
  md:grid-cols-2`; one card uses the full available grid width. Each mini-card shows the provider
  heading, the comparable MXN price, the winner label when green, the source link/plain label and
  **its own** observation date (`Observado <fecha>` / `Sin fecha de observación`). The winning card
  also carries a success-token border (`border-[color:var(--color-success)]`), so the winner reads
  without reading the figure. Steam is never a card. The comparison disclaimer remains below the
  grid. Empty state stays `Sin precio comparable en MXN por ahora.`.
- The two CTAs live in the action row under the hero header. There is no "Referencia histórica" zone:
  the historical figure remains in the hero price panel.

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
| always | no price claim, no discount and no savings math from ownership; ownership never feeds the comparator or `bestGroupOffer` |

### Discovery (`/discover`)

- Rendered inside `ProductShell` (`title="Descubrir"`, meta badge `Steam · México`). The page is a
  server component that redirects to `/login` without a session and delegates every state to
  `discover-client.tsx`; the route is protected by the global matcher in `middleware.ts`
  (`lib/security/route-policy.ts` is untouched). Nav entry «Descubrir» in `productNavItems`.
- One loading state (`app-card p-5 text-sm text-muted` "Cargando..."), one failure state
  (`Alert variant="danger"` + `Button variant="secondary"` "Reintentar"), then three sections fed by
  one parallel fetch each (`discount | historic | recent`, `pageSize` default server-side).
- Each section carries the shared kicker + `h2` + muted note shape: «Mayores descuentos» (by discount
  desc, price + `-N%` success badge), «En su mínimo histórico» (current price at the local low, price +
  `En su mínimo` success badge), «Recién observados» (by observation desc, price only, never green).
  Green follows the standing semantics: discount and at-low only; recency is not cheapness.
- Cards use an `app-card` shell and a horizontally centered decorative 16:9 `tiny_image` at two-thirds of
  the card width, preserving its ratio while reducing its visual area. They sit in a `grid gap-3
  md:grid-cols-2 xl:grid-cols-4` (one column on mobile, two on `md`, four on `xl`) and link to
  `/games/<appId>`. The compact card keeps
  every fact (name clamped to two lines, AppID/type, price, source, Steam base, discount badge,
  observed date, link) without becoming a banner. `Mejor precio` uses success only for a positive
  comparable saving; `Precio base Steam` remains secondary. Per-section Spanish empty states; no fake
  data, no new tokens, no hardcoded colors.

### Shared line with the game detail

- `/discover` and `/games/[steamAppId]` speak the same chrome: `.app-card` surfaces, kicker
  (`text-xs font-semibold uppercase tracking-widest text-muted`) + `text-xl` heading per section,
  `tabler-badge-*` tones with words (never tone alone), `deal-price` numerals, guarded `es-MX` dates.
- The detail's Steam source table now sits in the same `app-card space-y-4 p-5` section shape as every
  other section (kicker «Precio oficial», `h2` «Precio en Steam», inner `.table-shell`); the table, its
  `sr-only` caption and every cell are unchanged. No logic or copy moved: the restyle is JSX chrome only.

### Wishlist and library cards

- `/wishlist` and `/library` render the same capsule card as `/discover` (`app-card`, cover thumb, name,
  figures with `deal-price`, state badges, «Ver precios» to `/games/<appId>` where an AppID exists) in a
  `grid gap-3 md:grid-cols-2` (`/wishlist` adds `xl:grid-cols-3` with its compact card; `/library` stays
  at two columns): two columns on desktop, one on mobile. No `DataGrid` remains on either page
  (`/users` is its only consumer now); search, sort and paging live in a plain toolbar above the grid and
  keep the same `localStorage` keys the tables used for page size and visible sections.
- Green follows the standing semantics on both pages — wishlist discount badges and ≥7 deal scores, library
  GOTY — never a historical figure.

### Wishlist (`/wishlist`)

- Rendered inside `ProductShell` (`title="Wishlist de Steam"`). The page is a server component that
  redirects to `/login` without a session and delegates every state to `wishlist-client.tsx`; the route is
  protected by the global matcher in `middleware.ts` (`lib/security/route-policy.ts` is untouched).
- Two stacked zones: an `app-card-accent` summary (kicker "Sincronización", `text-xl` heading "Wishlist de
  Steam", the sync control, the state badges and the sync report) and one `app-card` items section.
- Summary badges: item count (`tabler-badge-muted`, only in state `ok`) plus "Última sincronización
  <fecha>" (`tabler-badge-info`) or "Sin fecha de sincronización" (`tabler-badge-warning`). Tone is never
  the only signal: every badge carries its words.
- Items: one compact capsule card per game (`WishlistCard`) in a `grid gap-3 md:grid-cols-2
  xl:grid-cols-3` — the `/discover` shell (`app-card` + cover thumb + name link + «Ver precios» link to
  `/games/<appId>`) — at every width. The sparse first surface shows the header row (selection
  checkbox, 90×34 cover below `sm` / 120×45 above, name, AppID/priority, category badges), one price
  line (`Mejor precio` + `Mayor descuento` + `Precio base`), the «Ver precios» link, then the category
  and freshness actions (`Editar categorías`, per-row `Sincronizar`, `Actualizado`). A native
  collapsed-by-default `<details>` zone («Más detalle») follows with the remaining facts (`Mínimo
  histórico`, `Mín. oficial`, `Mín. keys`, the four `% dto.`/`Deal` metrics and the
  `Alta`/`Actualizado`/per-provider sync badges). The row error stays visible outside the collapsed
  zone. The «Ya adquirido en» line renders under the header when the game is owned elsewhere. The
  game name is an internal `Link` to `/games/<appId>`; the cover is the local `WishlistThumb`
  (decorative `alt=""`, `Gamepad2` placeholder when the URL is missing or fails). The ITAD identity
  lives in the sync badges (the `IT` stamp); the Steam priority is a muted «Prioridad N» note beside
  the AppID, not a hidden column.
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
  deal scores immediately. Only page size and sorting are local; this threshold
  is not.
- **Filter and sorting:** the server owns paging and ordering (`page`/`pageSize`/`sort`/`direction`,
  mirrored in the URL and clamped to the supported sort keys); the cards render the current page only. The
  toolbar above the grid holds the draft search input (`Buscar por nombre o AppID`, applied with the filter
  panel), an `Ordenar por` select with the eight server sort keys plus `Sin orden`, and an `Asc`/`Desc`
  direction button. The pager below the grid (`Filas` 10/25/50/100, `Anterior`/`Siguiente`,
  `Página X de Y`) drives the same pagination state the table used, and the page size persists in
  `localStorage` (`wishlist.pageSize.v1`). There is no per-column filter row: the table rendered one, but in
  server mode it never filtered (the grid deliberately skips client filtering there, so it searched only the
  dead input, never the snapshot).

- **Package selection and subtotals:** each card carries a checkbox and an accent border while
  selected; a «Seleccionar página» checkbox above the grid selects the current page (with `indeterminate`
  when the page is partially selected). Selection is keyed by AppID in a
  `Set`, so sorting, filtering or paginating never moves a mark onto another game.
  `app/wishlist/_lib/wishlist-package.ts` (pure module, no imports) owns `toggleAppId`, `setAppIds`,
  `pageSelectionState`, `reconcileAppIds`, `packageRequestAppIds` and `exceedsPackageLimit`; it is covered by
  `wishlist-package.test.ts` (`node --test`). Selecting at least one game reveals a sticky
  `app-card-accent` bar with one card per scenario (`Todo en oficial` / `Todo en keys`), each printing its
  own MXN subtotal, its `N de M cotizados` count and its missing count, plus `Limpiar selección`, the
  `pricedAt` stamp and the note that the two figures are alternatives and never add up. The bar is
  `aria-live="polite"` and never shows a stale amount as current: while the request is in flight both cards
  read `Calculando...` and a scenario with nothing quoted reads `Sin cotizar`, never `MX$0.00`. The server's
  `unmatchedAppIds` are removed from the selection and reported. Above the 200-AppID cap the bar shows the
  message and no subtotals. The preview adds no rate-limit policy: the cap is the brake.
- **Sortable keys:** `Prioridad`, `Juego`, `Mejor precio`, `Mayor descuento`, `% dto. oficial`,
  `% dto. keys`, `Mín. oficial` and `Mín. keys` — exactly the server-supported keys (`WISHLIST_SORTS`);
  anything else clears the sort and returns to the server default order.
- **Null sorting:** a missing price is never coerced to zero — the card reads "—" — so it cannot appear as
  the cheapest row.
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
| row refresh failure | `role="alert"` `.text-danger` line under the card; the rest of the page and the previous prices stay untouched |
| row refresh rejected by the rate limiter | "Se alcanzó el límite de refrescos (6 por minuto por IP). Espera un minuto y vuelve a intentar." — a wait instruction, never the bare word "error" |
| row refresh failed for another reason | the upstream message plus the factual limit note, so a rejection always ends in a wait instruction |
| row refresh succeeded | the list is re-fetched with `getWishlist` and re-rendered in place; there is no navigation, so the scroll position is preserved |
| row refresh succeeded but the reload failed | the page-level `Alert variant="danger"` in the summary says the prices were updated and asks for a page reload |
| `state` missing or unknown | the whole response is rejected at the BFF (502 upstream error), never mapped to a guess |
| a sync report count missing or invalid | the report is rejected at the BFF; no count is defaulted to 0 |
| always | decorative covers with `alt=""` and the item name as adjacent text |

### Biblioteca (`/library`)

- Rendered inside `ProductShell` (`wide`, `title="Biblioteca de juegos"`). The server page calls
  `requireAdminSession()`, so the route is admin-only like `/users`; the global matcher already protects it.
- Two zones: an `app-card-accent` import card (kicker "Importación", the file input plus "Importar", and the
  report) and one `app-card` items card (counts, store filter, list). Both BFF routes are `AdminWithId`.
- **Import:** `<input type="file" accept=".json,application/json">` capped at 10 MiB
  (`LIBRARY_IMPORT_MAX_BYTES`) and checked in the browser first (valid JSON, non-empty array at the root)
  before the `POST`. The BFF re-checks the declared length and the real byte length, re-validates the root
  shape, and forwards the array untouched. The file is never stored and never re-read.
- **Filter and list:** one native labelled `<select>` for the store (`Todas las tiendas (N)`, then each store
  with its count) and a second one for the played year (`Todos los años`, each year, and `Sin año` only when
  something has no dated review), plus a button group `Filtro de estado de juego` with `Todos | Por jugar |
  Terminado | Completado 100% | Dropeado` where **every option carries its count** (the group is the report:
  "cuántos por estado"). Below the filters, a search input (`Buscar por juego o tienda`, accent-insensitive
  over title and store), an `Ordenar por` select (`Juego | Estado de juego | Última reseña | Años jugados`)
  with an `Asc`/`Desc` direction button, and the `Columnas` menu toggling each card section. The list is one
  capsule card per game in a `grid gap-3 md:grid-cols-2` (the `/discover` shell: `app-card` + cover + name +
  «Ver precios» to `/games/<steamAppId>` when the row carries a Steam appid). Each card shows the title, the
  store badges, the state tags (`En tu biblioteca` / `Wishlist` / Game Pass, plus `Instalado`), the
  play-status badge, the favorite toggle, the last-review badges (or "—"), the played-year badges (or "—")
  and the `Reseñas`/`Reseñar`, `Portada`/`Cambiar portada` and `Editar título` actions. A pager closes the
  list (`Filas` 10/25/50/100, `Anterior`/`Siguiente`, `Página X de Y`); the page size and the visible sections
  persist in `localStorage` (`library.pageSize.v1`, `library.columns.v1`, the same keys and shape the table
  used). Searching or sorting returns to the first page; the store/year/status filters do not move it.
- **Los conteos cuentan lo que se ve.** Tienda, estado y año se calculan sobre el conjunto ya filtrado por
  tienda, así que elegir una tienda reescribe los números de los otros dos filtros y nunca aparece un
  "12 juegos" que la grilla no pueda mostrar. Como un juego rejugado cuenta en cada año en que se jugó, la
  suma de los conteos por año puede superar el total de juegos; por eso el filtro de estado usa el total como
  referencia y el de año no.
- **Un filtro con un valor que ya no existe vuelve a "Todos".** Si una recarga deja la tienda o el año
  elegido sin filas, el filtro se resetea sola: un `<select>` con un valor ausente se pintaría vacío y la
  lista quedaría filtrada sin explicación.
- **Prices (`priceState`):** the row's right column renders `Alta` first and the price block under it, so the
  row keeps its two-line shape at 100 rows per view. `exact` and `title_candidate` render up to four values —
  `Oficial` and `Keys` are already MXN minor units (the backend converted them), `Base` and `Mín. histórico`
  come in `baseCurrency` and are **never** converted in the browser. The money primitive is the shared
  `components/ui/price-value.tsx` (`PriceValue`/`PriceFact`, moved out of `wishlist-client.tsx` unchanged and
  still used by `/wishlist`), so a missing amount reads "—" and no 0 is invented. A field whose amount or
  currency is null is omitted; if all four are missing the row shows one muted "Sin precio" and stays.
  `bindingSource` and `steamAppId` are normalized but never painted.
- **Estado de juego (sección `Estado de juego`).** `Por jugar | Terminado | Completado 100% | Dropeado`,
  derivado en el cliente con `playStatusOf`; el orden del selector es el del ciclo de vida
  (`Por jugar < Dropeado < Terminado < Completado 100%`), no el alfabético. «Por jugar» **no es una reseña**:
  es la ausencia de reseña, así que no se guarda en ninguna columna.
- **Años jugados (sección `Años jugados`).** Un badge por año, del más nuevo al más viejo, con el año de
  `finishedMonth ?? startedMonth` de **todas** las reseñas del juego; un juego rejugado muestra dos badges y
  ordena por el más reciente. Sin años, un `—` muted. El servidor manda los años ya calculados
  (`playedYears`), el cliente no deriva fechas.
- **Favorito (sección `Favorito`).** El interruptor es del **juego**, no de la reseña: marcar una tarjeta marca
  todas las que comparten `gameId`, y por eso la columna no depende de la plataforma elegida. Una fila sin
  `gameId` no ofrece el botón.
- **Portadas (`Sincronizar portadas` y `Portada` por fila).** La portada es del **juego canónico**
  (`games.image_url`) y se guarda como **URL**, nunca como imagen. El botón del toolbar hace una pasada
  acotada (25 juegos; el backend acepta 1–100) que recorre una **cadena de fuentes** —Steam, IGDB y
  SteamGridDB, en el orden que sugieren las tiendas del juego—, rellena solo lo vacío y **nunca reemplaza**
  una portada existente; el `Portada` de la fila abre el selector y esa elección sí reemplaza. Una fila sin
  `gameId` no ofrece la acción: no hay dónde guardar la portada y el motivo ya lo dice la acción de reseña.
  El selector ofrece **siempre los tres catálogos** —Steam, IGDB y SteamGridDB— y las tiendas del grupo solo
  deciden cuál llega preseleccionado: un grupo solo de PC propone Steam, uno solo de consola propone IGDB y
  uno mixto no propone ninguno. Busca por título (con el título precargado) y arranca sola en el catálogo
  elegido; la lista de SteamGridDB no trae miniatura —su autocomplete no la publica—, así que cada fila se
  identifica por id y nombre y la portada se resuelve al elegirla. Después de elegir, la URL nueva se pinta en
  todas las filas del mismo juego sin recargar; después de una pasada, la lista se recarga porque quien
  escribió fue el servidor.
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
| `playStatus === "backlog"` | `tabler-badge-neutral` "Por jugar"; es el estado de todo juego sin reseña y el único valor que no se puede guardar |
| `playStatus` desconocido | cae a `backlog` por el mismo camino que una reseña ausente: nunca se inventa un estado |
| `playedYears` con un año repetido o fuera de rango | el normalizador conserva solo años de cuatro dígitos, sin repetidos y ordenados del más nuevo al más viejo |
| toggle de favorito | optimista: la estrella cambia al pulsar, el botón queda `loading`, y un fallo revierte **todas** las filas del juego y deja un `Alert variant="danger"`; nunca se recarga la lista |
| fila sin `gameId` y sin portada | no se dibuja el botón de portada; la celda de acciones ya explica por qué la fila no es accionable |
| pasada de portadas en curso | el botón del toolbar queda `loading` y no se puede repetir mientras corre |
| pasada terminada | los cinco contadores como badges (`Puestas`, `Fallidas`, `Pendientes de otra pasada`, `Sin appid de Steam`, `Sin portada`); con `updated > 0` la biblioteca se recarga para traer las portadas guardadas |
| pasada o elección con error | `Alert variant="danger"` con el mensaje del BFF; la grilla y el selector quedan utilizables |
| portada de Steam remota | se pinta con `<img>` sin optimizador de Next, igual que el resto de la app: la URL es de un CDN ajeno |
| first load in flight | bordered "Cargando..." block at the resolved size |
| fetch failure | `Alert variant="danger"` plus "Reintentar"; the import card stays usable |
| file rejected before upload | `Alert variant="danger"` with the reason (over 10 MiB, not JSON, root is not an array, empty array); the input is cleared |
| import succeeded | the report renders and the list is re-fetched in place, with no navigation |
| import succeeded but the reload failed | the report stays and an `Alert variant="danger"` asks for a page reload |
| a report counter missing or invalid | the BFF answers 502; no counter is defaulted to 0 |
| `byStore` unusable | the breakdown is omitted; the four counters and the import result still render |
| always | never rendered: raw JSON, the uploaded file, `storeGameId`, BFF routes or credentials |

### Duplicados del catálogo (`/library/duplicates`)

Herramienta de mantenimiento admin-only, no una vista de producto: tabla densa, decisiones explícitas y
una operación **irreversible** (el juego absorbido se borra). No usa `DataGrid` —cada fila es un grupo con
unidad propia, no una página de datos— y no participa del comparador, `bestGroupOffer` ni ninguna cuenta
de ahorro. Contrato en `lib/contracts/games-merge.ts`, cliente en
`app/library/duplicates/_lib/games-merge-api.ts`, BFF `GET /api/bff/games/merge-suggestions` y
`POST /api/bff/games/[id]/merge`, ambos `AdminWithId`.

- Rendered inside `ProductShell` (`wide`, `title="Duplicados del catálogo"`). The server page calls
  `requireAdminSession()`, so the route is admin-only like `/library`; the global matcher already protects
  it and `route-policy.ts` is untouched. `productNavItems` gains the explicit `Duplicados` entry pointing
  at `/library/duplicates`; `/library` keeps its own active state because `isRouteActive` no longer treats
  a route with a nested nav entry as active for its children.
- La lista llega como un arreglo crudo de grupos (el BFF lo reenvía tal cual). El normalizador descarta
  grupos malformados y **cualquier grupo con menos de dos miembros válidos**, y una respuesta que no es
  arreglo es `null` → 502, nunca "no hay duplicados".
- Un grupo es `foldedTitle` + sus miembros con el **título real**, las tiendas con su `storeGameId` y los
  appids Steam. Las tiendas se pintan con `storeLabel`/`toStoreKey` (`lib/contracts/stores.ts`), el mismo
  vocabulario de la biblioteca; una tienda fuera del catálogo conserva su texto.
- **Elección de superviviente:** la primera columna de cada tabla de grupo es un `radio` por miembro, con
  `name` único por grupo (`survivor-<índice>`) y `aria-label` "Elegir «<título>» como el juego que
  sobrevive". El estado es un mapa `índice de grupo → gameId` (los grupos no traen id propio). Sin
  selección, el botón de acción dice "Elige el juego que sobrevive" y queda deshabilitado; con selección
  enumera los absorbidos en el texto de apoyo. Los radios se deshabilitan mientras hay una fusión en
  curso o el grupo está bloqueado.
- **Confirmación:** un `alertdialog` con los títulos reales, la lista de absorbidos con sus `gameId` y la
  frase "La fusión no se puede deshacer." Escape cancela (más click en el scrim), el foco inicial va al
  botón de confirmar y al cerrar vuelve al elemento que abrió el diálogo.
- **Fusión rechazada (409):** no es un error de red y no pasa por `parseApiError`. El 200 y el 409 se
  normalizan con el mismo `normalizeMergeResult`, que decide la rama por el booleano `applied`; el único
  motivo de rechazo es la identidad de Steam ambigua entre los dos juegos. El cuerpo no lleva una lista que
  resolver: el panel de error muestra `blockReason` y nada se reintenta en automático.
- **Las reseñas nunca se descartan.** `game_reviews` no tiene clave única por `(usuario, juego, plataforma)`,
  así que una reseña de cada lado simplemente convive en el superviviente. La fusión no pide decidir nada
  sobre reseñas ni las borra.
- **Fusión por secuencia:** el botón de grupo fusiona el resto de miembros en el superviviente con un
  `POST` por absorbido. La secuencia se detiene en el primer rechazo y, si un `POST` falla de otra forma, el
  panel de error **informa lo que sí se aplicó** en vez de fingir un rollback que no existe. Los grupos no
  se fusionan en una sola llamada porque el contrato es de a uno.
- Tras una fusión completa la lista se recarga; con una fusión parcial (un absorbed aplicado y luego
  error/pendiente) no se recarga, para no perder el contexto de lo que falta.
- `blocked: true` en el grupo **o** en cualquier miembro deshabilita la acción, marca el grupo con un badge
  de advertencia y escribe el motivo (`blockReason` del miembro; el grupo no trae texto propio). La UI
  nunca ofrece fusionar un grupo bloqueado.
- **Resultado:** el panel de resultado enumera cada absorbed con los contadores que el backend sí informó
  (un contador ausente se lee como "no informado", nunca como 0), con `role="status"` para el anuncio.

| Condition | Treatment |
|---|---|
| first load in flight | `app-card p-4 text-sm text-muted` "Cargando..." |
| fetch failure | `Alert variant="danger"` plus "Reintentar" |
| response shape invalid (not an array, or every group dropped) | the BFF answers 502; a broken shape is never read as "no duplicates" |
| 0 groups | "No hay grupos de duplicados pendientes." plus the explanation of what the catalog checked |
| group with ≥1 `blocked` member or `blocked: true` | warning badge "Bloqueado", the member's `blockReason` in text, every radio and the merge button disabled |
| member not blocked | muted "Disponible" badge; a member with no store refs or appids reads "—" in that cell |
| no survivor chosen | the merge button is disabled and reads "Elige el juego que sobrevive" |
| survivor chosen | the button names the count and the survivor; the helper line lists the absorbed titles |
| confirm dialog open | `alertdialog`, focus on "Sí, fusionar", Escape/scrim cancels, focus returns to the trigger |
| merge in flight | radios disabled, the group button `disabled`, the panel says "Fusionando en «<superviviente>»..." with `aria-live="polite"` |
| 409 (rejected merge) | the error panel shows `blockReason`; nothing is retried automatically and no review list is offered |
| merge applied for one member, another fails | the error panel keeps the applied list (what moved, per member) and does not claim a rollback |
| all merges applied | success panel, then the list is re-fetched; a done panel keeps the per-member counters |
| any count absent in the merge response | reads "sin contadores informados" for that member; a 0 is never invented |
| always | no raw JSON, no BFF paths, no env values, no credentials; the survivor and absorbed titles are the real ones from the payload |

### Reseñas por plataforma

- **Varias reseñas por `(juego, plataforma)`.** Un juego rejugado tiene una reseña por partida y todas
  conviven: no hay clave única ni comprobación de duplicado en el servicio. `platform` usa el vocabulario de
  `lib/contracts/stores.ts`, la misma llave de `user_library.store`, así que la reseña y la fila de
  biblioteca se cruzan sin traducción. El contrato vive en `lib/contracts/reviews.ts`; los meses viajan como
  `YYYY-MM` (mapean directo a `<input type="month">`), la nota es un entero 0–100 y `scoreLabel` llega
  calculada por el backend.
- **`scoreLabel` es solo de presentación y la calcula el servidor.** La UI no replica los umbrales de
  `ReviewScoreBands.Label`, no los conoce y no muestra una previsualización de la etiqueta mientras se
  escribe: la etiqueta aparece recién con la respuesta de la API después de guardar. Regla arquitectónica
  explícita.
- **Un solo editor, en el drawer de `/library`.** La grilla pinta una sola etiqueta por fila —la reseña
  más reciente de ese `(juego, plataforma)`, elegida con `newestReview`— y `Reseñas` / `Reseñar` abre el
  drawer del juego. El drawer pide la lista completa a `GET /api/bff/reviews` (la fila de biblioteca solo
  trae la última) y lista **todas** las reseñas de la plataforma elegida, cada una con sus propios
  `Editar esta reseña` y `Borrar`; `Nueva reseña` abre un formulario vacío sin tocar las anteriores. Guardar,
  editar o borrar mantiene el drawer abierto y actualiza en el sitio las filas que comparten
  `(gameId, platform)` con la reseña representativa que queda; no hay navegación ni recarga de la lista.
- **El estado viaja en la reseña y es obligatorio.** La partida se marca con un radio `Terminado |
  Completado 100% | Dropeado` (`REVIEW_STATUSES`); `Por jugar` no está en el grupo porque es la ausencia de
  reseña. Crear y editar exigen el campo —editar nunca debe perder el estado de la partida— y una reseña
  guardada antes de este cambio se lee como `finished` (el default de la columna). El detalle del juego pinta
  el estado de cada reseña junto a la plataforma.
- **GOTY es independiente de Game Pass.** El tag `GOTY` (tono success) es un logro de la reseña y nunca se
  mezcla con el tag de suscripción; un juego en Game Pass puede o no ser GOTY.
- **Una fila sin `gameId` no se puede reseñar.** No se oculta ni se ofrece una acción rota: la fila muestra
  el motivo honesto (el catálogo todavía no le da identidad canónica). Lo mismo si su `store` no está en el
  vocabulario de plataformas.
- **El detalle del juego es de solo lectura.** Muestra las reseñas de todas las plataformas del juego
  canónico después de la comparación de precios, en una tarjeta discreta y visualmente secundaria, sin
  duplicar el editor. Si no hay reseñas, la tarjeta no existe.
- **Campos aditivos.** `gameId`, `review`, `isFavorite` y `playedYears` se suman a cada item de
  `GET /api/library`; `reviews` e `isFavorite` se suman al payload del detalle de Steam.

### Portadas

- **Solo se escribe `games.image_url`, y solo como URL.** Ni identidad, ni precios, ni filas de biblioteca:
  una portada equivocada es un error cosmético que la siguiente elección arregla, y por eso ninguna
  operación de portada reclama identidad ni puede crear un juego canónico. La URL la resuelve siempre el
  servidor contra el proveedor (Steam `header_image`, IGDB o SteamGridDB): el cliente manda el id, nunca una
  URL propia. Que la pasada no escriba `game_external_ids` no es cosmético: una fila `steam` haría que el
  binding empezara a cotizar ese juego desde Steam a partir de una coincidencia de título.
- **Pasada acotada, no trabajo de fondo.** `POST /api/library/covers/sync` revisa hasta 25 juegos de la
  biblioteca del que llama y devuelve cuántos quedan; repetir el botón avanza. La cadena se para en la primera
  fuente con URL; `unmatched` (ninguna fuente tenía arte) y `failed` (alguna fuente no se pudo consultar)
  nunca abortan la pasada.
- **La elección manual es la única que reemplaza.** `PUT /api/games/{gameId}/cover` con `{ steamAppId }`,
  `{ igdbId }` o `{ steamGridDbId }`, el mismo selector para los juegos que la pasada automática no pudo
  resolver (`unmatched`). El selector ofrece las tres fuentes y el grupo solo preselecciona una; su búsqueda
  de candidatos en SteamGridDB (`GET /api/library/covers/steamgriddb/search`) es de solo lectura y sin
  miniatura: el autocomplete no publica arte, así que la portada se resuelve al elegir la fila.

### Favoritos

- **Tabla propia del usuario, no una columna de la reseña.** El favorito vive en `user_game_favorites`
  (`user_id, game_id`, PK compuesta) y no en `game_reviews`: con varias reseñas por juego no habría forma de
  decir cuál manda. Desmarcar **borra la fila**, no escribe un `false`.
- **La identidad viaja una sola vez.** `POST`/`DELETE /api/bff/favorites` aceptan `gameId` (la fila de
  biblioteca, que ya tiene el id canónico) o `steamAppId` (el detalle de Steam, que no lo tiene a mano), nunca
  los dos ni ninguno; `parseFavoriteTarget` lo rechaza antes de salir y el servidor responde 400. Un juego que
  el catálogo todavía no reconoce no se puede marcar.
- **Idempotente y optimista.** Marcar dos veces o desmarcar lo que no estaba no es un error, y la UI actualiza
  el estado sin recargar: el fallo se ve como un aviso y una reversión, no como una lista recargada. Los normalizadores toleran su ausencia (`null` / arreglo vacío) y nunca inventan una
  reseña. La `review` de una fila —la más reciente del par— solo se pinta si su `gameId` y `platform`
  coinciden con la fila. La nota
  se acepta solo como entero 0–100 y el mes solo como `YYYY-MM`; cualquier otra forma degrada a `null`, y
  `isGoty` solo es verdadero con el literal `true`.

### DataGrid (`components/data-grid/data-grid.tsx`)

Used by `/users` (admin). Props are additive-only and the new capabilities are
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
- **Refresh (offers):** "Actualizar ofertas" lives once in the game summary card, in the row that carries
  the update date. It calls `refreshSteamGame` (`app/steam/_lib/steam-api.ts`),
  a `POST` to `/api/bff/steam/games/<appId>` through `csrfFetch`, and refreshes both providers at once.
  A dedicated `refreshing` flag drives only the button's `loading`/`aria-busy`, so the page never
  returns to the full-page "Cargando..." state and the groups stay readable during the call. An
  `aria-live` `polite` status line shows "Consultando tiendas..." while it runs.
- **Refresh failure (offers):** inline `Alert variant="danger"` in the summary's refresh row; the
  previously loaded game and offers stay on screen. A failed refresh never blanks the detail page, and a
  failed provider never empties the other provider's group.
- **Cheapest highlight (offers):** the highlight is text-only — a `tabler-badge-success` "Más barato"
  plus `.text-success` on the price and the `Aprox. MXN` value (ITAD table cells, gg.deals list entries)
  — and it is scoped **per provider group**;
  the tally never compares an ITAD row against a gg.deals bucket. No background or border is added to the
  `<tr>`/`<li>`, so `.table-row:hover` (a `@layer components` rule) and the list separators keep working;
  a utility background would
  outrank it and kill the hover feedback.
- **Best price cards:** static content, no interaction beyond each card's external store link. When
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
- Provider tabs: `role=tablist` labelled "Proveedores"; each tab carries `role=tab`, `aria-selected`,
  `aria-controls` and a roving `tabIndex`, each panel `role=tabpanel` with `aria-labelledby` pointing
  back at its tab. ArrowLeft/ArrowRight/Home/End move selection and focus; every tab has a visible
  `focus-visible` ring.
- Offer state is always carried by text ("Precio actual", "Datos incompletos", "Sin fecha de
  actualización") in addition to color; the same applies to classification ("Oficial", "Keyshop").
- "Más barato" is a visible badge, so the cheapest rows are not signalled by green text alone; its
  `sr-only` tail ("entre las tiendas comparadas en MXN de este proveedor") scopes the claim for screen
  readers. DRM and
  platform badges sit behind `sr-only` legends, and the names hidden by `+N` are read out in full.
- The `Mejor precio comparable` cards name their comparison basis in visible text — each card's kicker is
  its provider group's heading — so the summary is never understood as covering every store, every offer
  or both providers at once. The FX reference strip is a labelled region whose heading states that the
  rate is a conversion reference, so `≈` is never read as the store's regional price.
- The summary's refresh control is the only one on the page and carries a visible name
  ("Actualizar ofertas" / "Actualizando...") plus its live status line, so a keyboard or screen-reader
  user reaches it once, in the game summary, before the offers section.
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
- **Highlight vs. cards.** The `Más barato` badge marks the cheapest row of **one provider group**; the
  best-price cards show one card per provider with a comparable offer and **never** a Steam card, because
  Steam's direct price is the page headline and the referee of the green label. Copy must keep the two
  claims distinguishable.
- **Historical-low semantics differ by source.** Steam's `lowestPriceMinor` / `lowestPriceAt` is only
  a locally observed low in real MXN, never a provider history. ITAD's `historyLow*` is one game-level
  provider low duplicated across shop rows; the table labels it `mínimo histórico ITAD (juego)` so it
  cannot look shop-specific. gg.deals history is per aggregate bucket (`retail` / `keyshop`) and stays
  in each list entry. The summary's sources historical minimum (`Mínimo histórico de las fuentes`)
  chooses the lowest safely comparable
  candidate across these sources; USD is converted only when the offer's historical currency and
  `originalCurrency` are both USD and `fxRate` exists. It is approximate when converted, sources and
  regions differ, and it is a guide — never call it a universal historical low or an all-time low. It is
  rendered as a neutral figure, never in the green that marks "cheapest right now", and it is kept
  separate from the local Steam minimum so one number never wears the other's label.
- **DRM and platform names come from ITAD** (`drmNames` / `platformNames`); gg.deals returns neither, so
  its rows render no such badges. They may be empty or unknown
  strings: empty arrays render nothing, unknown names render verbatim, and there is no expand/collapse UI
  — the `+N` remainder is only exposed to assistive tech. Add a popover only against a real need.
- **Classification allow-list and validator are the same list.** `STEAM_OFFER_CLASSIFICATIONS` and
  `toClassification` must be extended together: `normalizeOffer` returns `null` when a classification
  does not validate, so a provider-specific value added in only one of the two places drops every row of
  that kind with no build error and no visible failure. That is how `keyshop` could have disappeared.
- **Almost no unit tests.** `bestGroupOffer` / `cheapestTies` are pure functions on purpose so they can be
  covered the day a test runner exists. `node --test` now covers two of these pure modules —
  `app/games/[steamAppId]/_lib/bundle-card.test.ts` (`pickPricedBundleTier`) and the wishlist `_lib` pair —
  and the rest is still uncovered. Run them from inside the `_lib` directory: `node --test bundle-card.test.ts`;
  from the repo root the bracketed path is read as a glob and matches nothing, which reports
  `tests 0` and **succeeds**.
- **Wishlist pages on the server.** The API returns one page of the snapshot and the page renders it
  as capsule cards; the normalizer (`app/wishlist/_lib/wishlist-contract.ts`) caps a payload at 5000 items and
  drops unknown/extra fields. Widen the page sizes only against a real oversized account, not speculatively.
- **Two wishlist trust boundaries, one shape.** The BFF route and the client share the same normalizer, so
  both reject an invalid `state` and default a malformed item to "dropped" instead of rendering it. The
  client re-validates what the BFF already normalized: cheap, and it keeps a future non-BFF consumer honest.
- **`WishlistThumb` duplicates `SteamThumb`.** Both are local components with the same 120×45 recipe;
  extract one shared component when a third consumer appears. Same `<img>` debt as the cover note above
  (`next.config.ts` has no `images.remotePatterns`).
- **Manual sync only.** The wishlist page never polls and never syncs on load: a snapshot older than the
  last manual run is shown with its own `syncedAt` badge, and "Sin fecha de sincronización" when the API
  reports none. Scheduled refresh is out of scope.
- **Server cuts in the URL, draft search on the toolbar.** The store/price/category cuts travel as
  query parameters and filter on the server; the name/AppID search is a draft applied with the filter panel.
  Add more server cuts only when list size or measured render cost makes this boundary real.
- **The library is unpaged on the server and filtered in the browser.** The API returns the whole
  `user_library` snapshot (~2.6k rows), so `/library` filters by store client-side and pages it 10/25/50/100
  with `Anterior`/`Siguiente`. The normalizer (`app/library/_lib/library-contract.ts`) caps a payload at 20000 items and
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
  `game_offers`, `bestGroupOffer` or any savings math, are never compared across rows, and a row with no
  observation shows "—" for all four instead of a stale or invented number.
- **Verification.** No test project exists in this repo, so the check is `pnpm build` plus manual
  browser QA (owed: home/search/detail at light and dark, mobile drawer keyboard walkthrough, and the
  offers section with: the ITAD table and the gg.deals aggregate list side by side, a keyshop bucket
  (badged) next to a `retail` bucket (no badge, and not readable as an error thanks to the group note),
  a game where only one provider has rows, tied cheapest offers inside one group, a `fx_estimate` offer
  with its page-level FX strip (and a game with none, where the strip must not exist), an `unconverted`
  offer, an offer with and without historical low, per-card observation dates on different days, a game
  with and without a sources historical minimum, DRM/platform
  badges with more than two names, either stale badge, both attribution links, an empty result and a
  failed refresh, at 360px). No Lighthouse
  or visual-regression run was executed.
