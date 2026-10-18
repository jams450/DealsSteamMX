# Global ITAD deals feed (spec S1/S4, task T7)

Scope: implement the global ITAD deals discovery explicitly authorized by S4 ("pues ejecuta todo y dime
al terminar", layer L13) as task T7. The feed surfaces **good individual deals for games outside the
authenticated user's wishlist**. It is deliberately separate from the bundle work (T2/T3): deals live in
their own table, under their own source, and never enter `game_offers`, `external_bundles` or any
savings math.

Nothing here is certified against a live provider with a key: no real key was used and no real send
happened. The **response shape is taken from the primary published contract** rather than invented:
IsThereAnyDeal API 2.11.0 OpenAPI at `https://docs.isthereanydeal.com/openapi.yaml` (and
`openapi.json`), `GET /deals/v2` -> schema `resp.deals` plus its published example
`resp.deals.get.v2.success`. The fixtures below are shaped after that published contract and are still
not live captures. The `sort` token value set remains uncertified.

## Provider contract

```http
GET https://api.isthereanydeal.com/deals/v2
    ?country=MX
    &shops=<OfficialShopIds>
    &sort=-cut
    &offset=<n>
    &limit=<1..200>
ITAD-API-Key: <key>
```

- `country` (ISO 3166-1 alpha-2), `offset`, `limit` (1..200), `sort`, `shops` (comma-separated shop ids)
  are the documented query parameters. `nondeals` and `mature` default to `false`, so only active deals
  for non-mature content are requested.
- The API key travels only in the `ITAD-API-Key` header, never in the URL.
- The request is admitted by the **same** `ItadRequestGovernor` singleton the per-game client uses
  (`ItadGlobalDealClient` is a new typed class; `IItadClient` is unchanged), so the whole ITAD surface
  shares one local budget. A `429` is retried once honoring `Retry-After`, exactly like the POST paths.
- `limit` is clamped to 200 before the request; a larger value is never sent.
- `IItadClient` / `ItadClient` were **not** modified.

### Published response shape (from the OpenAPI schema, not a live capture)

`resp.deals` requires `nextOffset` (int), `hasMore` (bool) and `list` (array). Each list item requires
`id` (the **game** UUID), `slug`, `title`, `type` (`game` | `dlc` | `package` | null), a nested
required `deal`, and optional `mature`/`assets`. The `deal` object requires
`shop`/`price`/`regular`/`cut`/`voucher`/`storeLow`/`historyLow`/`historyLow_1y`/`historyLow_3m`/`flag`/
`drm`/`platforms`/`timestamp`/`expiry`/`url`. `historyLow` is a price object or null, and `price` is
`{amount, amountInt, currency}` with `currency` matching `[A-Z]{3}`. There is **no deal id** in the
contract.

```json
{
  "nextOffset": 3,
  "hasMore": true,
  "list": [
    {
      "id": "018d937e-e9ec-7103-97a0-732d4301eb72",
      "slug": "shift-quantum",
      "title": "Shift Quantum",
      "type": "game",
      "mature": false,
      "assets": { "boxart": "https://assets.isthereanydeal.com/018d937e-e9ec-7103-97a0-732d4301eb72/boxart.jpg" },
      "deal": {
        "shop": { "id": 50, "name": "Nuuvem" },
        "price": { "amount": 11.4, "amountInt": 1140, "currency": "BRL" },
        "regular": { "amount": 37.99, "amountInt": 3799, "currency": "BRL" },
        "cut": 70,
        "voucher": null,
        "storeLow": { "amount": 9.49, "amountInt": 949, "currency": "BRL" },
        "historyLow": { "amount": 7.59, "amountInt": 759, "currency": "BRL" },
        "historyLow_1y": { "amount": 9.49, "amountInt": 949, "currency": "BRL" },
        "historyLow_3m": { "amount": 9.49, "amountInt": 949, "currency": "BRL" },
        "flag": null,
        "drm": [ { "id": 61, "name": "Steam" } ],
        "platforms": [ { "id": 1, "name": "Windows" } ],
        "timestamp": "2024-12-05T04:26:01+01:00",
        "expiry": "2024-12-19T03:59:59+01:00",
        "url": "https://itad.link/018d9386-c580-739b-bac6-06f2a7eb5d61/"
      }
    }
  ]
}
```

This sample is the published Shift Quantum example (with its `expiry` moved forward to stay active); it
is schema-shaped, not a live capture. `historyLow` is read **only when the provider actually reports
it** and is never synthesized.

## Parsing rules (`ItadGlobalDealClient`)

- The envelope must be an object with a `list` array and a boolean `hasMore`; anything else throws and is
  never read as "no deals". A short page cannot be told from a truncated one without the provider flag.
- The game identity is at the **list-item level**: `id` (UUID), `title`, `slug`, `type`, `mature`. The
  offer is the **nested `deal` object**: `shop`, `price`, `regular`, `cut`, `historyLow`, `expiry`, `url`.
- The snapshot key (`deal_key`) is **composed** as `game id + ":" + shop id`: the contract publishes no
  deal id, and this is stable across syncs for a current price at a shop. A repeated page key is
  deduplicated.
- Structural violations on a deal throw: a non-object entry, a missing/oversized/non-UUID `id`, a
  missing `title`, a missing nested `deal`, a missing shop id/name, a malformed currency (not three
  ASCII letters) or a missing/negative `deal.price.amountInt`.
- `deal.expiry` is parsed explicitly as UTC. A present but unreadable expiry throws; a genuinely expired
  deal is dropped (it is not active).
- Free text is sanitized (control characters stripped, bounded); identity fields are rejected when
  oversized. `deal.url` stays verbatim (HTTPS only), so the ITAD affiliate tag survives.
- The discount is the documented `deal.cut` when it is 0..100; otherwise it is computed from
  `deal.regular.amountInt` vs `deal.price.amountInt` when both exist; otherwise it is `null`. No discount
  is invented.
- `historyLow` is read as the direct price object it is (`{amountInt, currency}`), not as
  `historyLow.all`.
- `OfficialShopIds` is the configured allowlist. A shop outside it is parsed but **not** persisted; the
  code never trusts the provider to honor the `shops` filter.
- Only `type == "game"` is part of the games feed; `dlc`, `package` and null are stored but never
  qualify as a good deal.
- Credential-free errors: fixed messages ("IsThereAnyDeal deals returned a malformed response." /
  "IsThereAnyDeal deals is unavailable.") never echo the key, the URL or the payload.

## Sweep completion (`GlobalDealSweep`)

The pure accumulator decides whether the pages fetched so far form an authoritative snapshot. It uses
the provider's explicit `hasMore`, not a short-page heuristic.

| Case | Result |
|---|---|
| Provider reports `hasMore = false` | Complete |
| `hasMore = true` but `nextOffset` repeats or does not advance | `CursorLoop` (incomplete) |
| A page's key set repeats an earlier page | `RepeatedPage` (incomplete) |
| Provider still reports more at `MaxPages` | `PageBudgetExhausted` (incomplete) |
| A page request or parse throws | `PageFailure` (incomplete) |

Only `Complete` reaches the writer. Every other outcome returns a preserved result: **no write, no
advanced timestamp, previous snapshot stands**. Missing `nextOffset` falls back to
`offset + observed items`; the sweep still owns the advance guard.

## Snapshot persistence (`EfGlobalDealSnapshotWriter`)

- Source is the dedicated literal **`itad_global_deals`** (own `global_deals` table). It is distinct
  from `itad` (per-game orphan purge) and `itad_global` (the bundle sweep), so no other provider cleanup
  can delete it and it deletes only its own rows.
- One transaction replaces the whole source: each deal is upserted with
  `INSERT ... ON CONFLICT (source, deal_key) DO UPDATE`, then rows of the same source not returned are
  deleted. Offers, bundles and other sources are untouched.
- Provider HTTP happens **before** the transaction. The transaction only writes the already-parsed,
  already-projected snapshot in memory.

## Currency projection (`GlobalDealCurrency`)

- Original currency and amount are the source of truth and are always stored.
- `MXN` quote → `pricing_type = 'regional'`, MXN amount = original.
- `USD` quote with a persisted rate → `pricing_type = 'fx_estimate'`; the MXN amount is the original
  multiplied by the latest persisted rate (`IFxRateService.GetLatestRateAsync`, **read-only, never a
  provider fetch**) with away-from-zero rounding and an overflow guard.
- Any other currency, or USD with no persisted rate → `pricing_type = 'unconverted'` with
  `mxn_current_price_minor = NULL`. There is no fabricated cross rate and no zero.

## Good-deal policy (`GlobalDealPolicy`)

A deal is surfaced only when it is a base game (`type == "game"`; `dlc`, `package` and null are
stored but never qualify), has a usable MXN amount (`regional` or `fx_estimate`) **and** either:

1. the documented discount clears the configured threshold (`GlobalDeals:MinDiscountPercent`, default
   50), or
2. the current original price is at or below the provider-reported `historyLow` price object quoted in
   the **same currency** as the price.

There is **no numeric score and no rating**: `deals/v2` publishes none, and manufacturing one from a
single discount would be a fabricated quality signal. `unconverted` foreign prices are not eligible for
the price threshold. The catalog returns a machine code (`discount` / `history_low`) that the UI copies
to text.

## Catalog read (`GlobalDealCatalogService`)

- Answers `GET /api/deals` from persisted rows only: no provider call, no refresh per render.
- Excludes the current user's wishlist games by exact ITAD game id (`steam_games.itad_game_id`).
  Resolution goes `user_library` (store `steam`, state `wished`) → Steam appid → MX `steam_games` row;
  it never reads the unmaintained `user_library.itad_game_id`.
- **Honest unknown match**: a wished game whose `steam_games.itad_game_id` is null has no ITAD identity
  we can compare against, so a deal that happens to belong to it is **not** excluded and may appear. That
  is an "unknown match", not a proven non-wishlist game; the feed's copy and this document say so
  instead of claiming the exclusion is complete. The rule is best-effort by exact identity only.
- Paginated honestly (`page`, `pageSize` default 20 / max 50, `total`, `hasMore`), ordered by discount
  desc, then newest observed, then deal key.

## Background job (`GlobalDealSyncJob`)

- **Disabled by default**: `GlobalDeals:Enabled=false` means the job returns immediately. Activate it
  explicitly after the migration is applied.
- Startup delay, then a pass, then a scheduled interval of `RefreshHours` plus random `JitterMinutes`.
- A PostgreSQL session advisory lock (key `7_831_442_095`, distinct from the wishlist sync
  `7_831_442_091`, ntfy deals `7_831_442_092` and global bundle sweep `7_831_442_093`) serializes the
  cycle across instances. A run that cannot take the lock is skipped, not queued.
- The startup (recovery) pass is gated by `MinHoursBetweenRuns` through `job_runs`; the scheduled slot is
  never gated. Each cycle is recorded under `deals.global-sync`; a preserved sweep is recorded as
  `failed` with its reason.

## Required wiring (parent-owned)

`ServiceCollectionExtensions` must add:

```csharp
services.AddOptions<GlobalDealOptions>()
    .Validate(o => o.PageSize is > 0 and <= GlobalDealOptions.MaxPageSize,
        "GlobalDeals:PageSize must be between 1 and 200.")
    .Validate(o => o.MaxPages > 0, "GlobalDeals:MaxPages must be greater than zero.")
    .Validate(o => o.RefreshHours > 0, "GlobalDeals:RefreshHours must be greater than zero.")
    .Validate(o => o.JitterMinutes is >= 0 and <= 1440,
        "GlobalDeals:JitterMinutes must be between 0 and 1440.")
    .Validate(o => o.StartupDelaySeconds >= 0, "GlobalDeals:StartupDelaySeconds must not be negative.")
    .Validate(o => o.MinHoursBetweenRuns >= 0, "GlobalDeals:MinHoursBetweenRuns must not be negative.")
    .Validate(o => o.MinDiscountPercent is >= 0 and <= 100,
        "GlobalDeals:MinDiscountPercent must be between 0 and 100.")
    .ValidateOnStart();

services.AddSingleton(sp =>
{
    var global = sp.GetRequiredService<IOptions<GlobalDealOptions>>().Value;
    return new GlobalDealSyncSettings(global.PageSize, global.MaxPages);
});
services.AddSingleton(sp =>
{
    var global = sp.GetRequiredService<IOptions<GlobalDealOptions>>().Value;
    return new GlobalDealCatalogSettings(global.MinDiscountPercent);
});

// Reuses ItadClientSettings and the SAME ItadRequestGovernor singleton.
services.AddHttpClient<IGlobalDealClient, ItadGlobalDealClient>((sp, client) => { /* ITAD base URL + timeout */ });

services.AddScoped<IGlobalDealSnapshotWriter, EfGlobalDealSnapshotWriter>();
services.AddScoped<GlobalDealSyncService>();
services.AddScoped<GlobalDealCatalogService>();
services.AddHostedService<GlobalDealSyncJob>();
```

`AppDbContext` must also add, parent-owned:

```csharp
public DbSet<GlobalDeal> GlobalDeals { get; set; } = null!;
// OnModelCreating:
modelBuilder.Entity<GlobalDeal>(entity =>
{
    entity.HasIndex(e => new { e.Source, e.DealKey }).IsUnique();
    entity.HasIndex(e => e.ItadGameId);
});
```

`SQL/schema.sql` should receive the `global_deals` block equivalent to
`SQL/migrations/2026-10-17_global_deals.sql`. `nav-config.ts` needs the `/deals` entry (parent-owned;
the route is protected by default because it is absent from `route-policy.ts`). No Compose variable is
required for correctness, but the documented `GlobalDeals__*` variables should be propagated.

## Configuration (env or appsettings)

```dotenv
GlobalDeals__Enabled=false
GlobalDeals__PageSize=200
GlobalDeals__MaxPages=20
GlobalDeals__RefreshHours=24
GlobalDeals__JitterMinutes=30
GlobalDeals__StartupDelaySeconds=300
GlobalDeals__MinHoursBetweenRuns=12
GlobalDeals__MinDiscountPercent=50
```

No new secret is required: the feed reuses `ItadOptions` (`ITAD:ApiKey`, `ITAD:BaseUrl`, `ITAD:Country`,
`ITAD:OfficialShopIds`, `ITAD:TimeoutSeconds`) and the persisted `fx_rates` rows.

## Gaps and risks

- **No live `deals/v2` capture exists.** The response shape now follows the primary published OpenAPI
  (IsThereAnyDeal API 2.11.0, `GET /deals/v2` -> `resp.deals` + `resp.deals.get.v2.success`), not an
  invented schema. Runtime certification against a real key and payload is still pending, and the `sort`
  token value set is uncertified (`-cut` is the single constant to change). `historyLow` is now read as
  the price object the schema publishes; it stays null when the provider omits it and is never
  synthesized.
- The deal snapshot key is **composed** (`game id:shop id`) because the contract publishes no deal id;
  it is a documented client identity, not a provider field.
- The EF writer's raw SQL (`ON CONFLICT`, transaction, source-scoped delete) and the catalog query are
  not executed by the checks: there is no live database here. The deterministic checks cover the pure
  sweep, the public parser, the service apply-only-on-complete rule, the policy/currency/wishlist rules
  and the source/lock isolation; the SQL shape is reviewed, not run.
- Blocking `429`/`5xx` on every page preserves the snapshot indefinitely, invisible except in logs and
  `job_runs`.
- A `deals/v2` page is global; a single malformed deal fails the whole page (preserve) rather than
  silently shrinking the catalog. That is intentional but means a provider data bug shows as "no refresh"
  until it is fixed.
- The frontend contract test runs with `node --test` and does not type-check. `pnpm` is unavailable in
  this environment, so `next build`/eslint were not run here.

## Key Learnings

- The first fixture was invented from a hand-written summary and had the game nested under a `game`
  object with a top-level `price`. The primary OpenAPI (`resp.deals` + `resp.deals.get.v2.success`)
  places the game fields on the list item and the offer under a nested `deal`; the parser was corrected
  against that source, and the invented shape is now a regression test that must be rejected. A
  schema-shaped test is only as good as the schema it was copied from.
- The provider flag (`hasMore`) is the boundary; a short page is not. The pure sweep keyed on
  `hasMore`/`nextOffset` is what makes a cursor loop and a budget truncation testable without a provider.
- The repository's measured trap repeats here: wishlist identity must resolve through `steam_games`,
  never through the unmaintained `user_library.itad_game_id`.
- A single new field (`cut`) is the only honest discount signal in the contract. Building a score from
  it, or inferring a history low from our own observations, would be the exact fabrication the task
  forbids.
- Reusing `ItadClientSettings` and the same `ItadRequestGovernor` singleton is what keeps a second ITAD
  surface from quietly doubling the provider budget.
- A dedicated source literal plus a dedicated advisory lock key are the two cheap isolation mechanisms:
  one keeps other providers' cleanup away from this table, the other keeps four background jobs from
  skipping each other.
