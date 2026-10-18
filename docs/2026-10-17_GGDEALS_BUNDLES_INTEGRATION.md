# GG.deals active bundles integration (T6, S4)

Last updated: 2026-10-17. Work unit: `odd/tasks/global-bundles.md` T6 (`S4`, expansion of `L7`).
Contract source: `docs/2026-10-17_GGDEALS_BUNDLES_API.md` (user-provided copy of the GG.deals API guide,
not a live fixture). Nothing here is certified against a live provider: no API key is used, no real
send happens, and every fixture in the checks is synthetic and marked as such.

This is the gg.deals *bundles* integration only. It is separate from the ITAD global sweep
(`docs/2026-10-17_GLOBAL_BUNDLES.md`), from the ITAD deals feed (`T7`) and from per-game offers. A bundle
never enters `game_offers` and never takes part in the savings math.

## Scope: what S# the change covers

- **S4** ("pues ejecuta todo y dime al terminar"): implemented the gg.deals active-catalog ingestion end
  to end (client, sweep, isolated snapshot writer, catalog read, UI badge) behind a default-off job.
- **S1** ("de las fuentes que tenemos ... colocar todos los bundles aunque no tengan algo de wishlist"):
  gg.deals bundles now appear as catalog cards with or without a wishlist match, tagged with their own
  source.
- **Not claimed: S2/S3 wishlist highlight for gg.deals.** GG.deals documents no ITAD UUID and no Steam
  appid, and no title matching is invented, so gg.deals bundles carry no `wishlistMatches`. That is the
  honest consequence of the missing identity, not a partial implementation.

## Provider contract (verified against the documented copy)

```http
GET https://api.gg.deals/v1/bundles/active/?key=<API_KEY>&region=us
```

- Base is `https://api.gg.deals/v1/`; the key travels in the query string (the provider documents no
  header). `region` is optional and has **no `mx`** value; the reference region is `us`. Per AGENTS.md
  rule 2, the allowed-region list was read from the sourced doc; the account/key and live responses stay
  unverified.
- `data` carries `previous`, `next`, `totalCount` and `bundles[]`. `next` is the cursor to follow and is
  the only pagination mechanism (no configurable page size, no stable bundle id).
- `Bundle` is `title`, `url` (nullable), `dateFrom` (UTC), `dateTo` (UTC or null) and `tiers[]`.
- `BundleTier` is `price` (decimal string), `currency`, `gamesCount` (int or null) and `games[]`.
  `BundleTierGame` documents only `title` and `url`.
- Quota is 100 records/minute and 1000/hour per key; each `active/` request costs **one record** no
  matter how many bundles it returns.

### Synthetic sample (schema-shaped, not a recorded response)

```json
{
  "success": true,
  "data": {
    "previous": null,
    "next": "https://api.gg.deals/v1/bundles/active/?key=<redacted>&region=us&offset=100",
    "totalCount": 1,
    "bundles": [
      {
        "title": "Synthetic Bundle",
        "url": "https://gg.deals/bundle/synthetic/",
        "dateFrom": "2026-10-01 00:00:00",
        "dateTo": null,
        "tiers": [
          { "price": "19.99", "currency": "usd", "gamesCount": 2,
            "games": [ { "title": "Game A", "url": "https://gg.deals/game/a/" },
                       { "title": "Game B", "url": null } ] }
        ]
      }
    ]
  }
}
```

The ids, titles and figures are invented for the checks; the key is shown as `<redacted>` on purpose.

## Parsing rules (`GgDealsClient.GetActiveBundlesAsync`)

- The root must be an object with `success: true` and an object `data`; `totalCount` must be a
  non-negative integer and `bundles` an array. Anything else throws a fixed-message `JsonException`, so a
  partial page can never be mistaken for an empty catalog.
- A non-object bundle entry, a missing title, a missing/unreadable `dateFrom`, an unreadable `dateTo`, an
  invalid or non-HTTPS URL, a non-array `tiers` or a tier whose `price` is present without a currency all
  fail the page. A `url` of `null` is valid by contract.
- Dates are parsed with `AssumeUniversal | AdjustToUniversal` and normalized to `DateTimeKind.Utc`; the
  documented example has no suffix, and treating it as local time would shift every window.
- Prices are decimal **strings**, parsed with invariant culture and converted to minor units with the
  same `ToMinorUnits` helper the per-game prices use. `gamesCount` is preserved exactly, including a
  value outside the listed set; bounding happens in the mapper (below).
- A bundle whose `dateTo` is in the past is dropped from the active snapshot.
- The API key and the cursor URL are never logged and never placed in an exception message: errors use
  the fixed `MalformedPayloadMessage` / `UnsuccessfulPayloadMessage` / "GG.deals active bundles are
  unavailable." strings.

## Cursor guards

A followed `next` link must be an absolute **HTTPS** URL with host `api.gg.deals`, path exactly
`/v1/bundles/active/`, the default port, and no user info and no fragment. Anything else is rejected
before the request, so the key can never be redirected to a third party.

## Heuristic bundle key (`GgDealsBundleKey`)

GG.deals documents no unique bundle id and its `url` is nullable, so identity is a **client-owned
heuristic**: `gg:` + the first 40 hex chars of a SHA-256 over the normalized title, the UTC start date
and the tier price/currency/selection summary. It is versioned by the prefix so changing the derivation
is explicit, and the prefix plus the source-family grouping (below) are two independent guards against
colliding with an ITAD key. This is *not* a vendor identifier and is documented as a heuristic.

## Sweep completion (`GgDealsBundleSweep`)

The pure accumulator decides whether the pages fetched so far form an authoritative snapshot. It takes
no I/O, so the rule is deterministic and testable without a provider or a database.

| Case | Result |
|---|---|
| `next: null` and collected distinct total == provider `totalCount` | Complete |
| A `next` cursor already followed | `RepeatedCursor` (incomplete) |
| A page whose bundle-key fingerprint repeats an earlier page | `RepeatedPage` (incomplete) |
| `totalCount` changes between pages, or the final count differs | `TotalMismatch` (incomplete) |
| `next` still present at `MaxPages` | `PageBudgetExhausted` (incomplete) |
| More than one hour elapsed since the first accepted page | `CursorExpired` (incomplete; **no restart**) |
| A page request or parse throws | `PageFailure` (incomplete) |

Only `Complete` reaches the writer. Every other outcome returns a preserved result: **no write, no
advanced timestamp, previous snapshot stands**. On `CursorExpired` the service stops; it never restarts
from the first page in the same run.

## Snapshot persistence (`EfGgDealsBundleSnapshotWriter`)

- Source is the dedicated literal **`ggdeals`** (7 chars, fits `VARCHAR(16)`; **no migration needed**).
  It is isolated from the `itad` per-game orphan purge and the `itad_global` sweep: this writer deletes
  only `ggdeals` rows and no other source's purge touches them.
- One transaction replaces the whole source: each bundle is upserted with
  `INSERT ... ON CONFLICT (source, bundle_key) DO UPDATE`, then rows of the same source not returned are
  deleted. Provider HTTP happens **before** the transaction.
- `deal_url` is always null (GG.deals publishes no affiliate redirector); the nullable article URL is
  stored in `page_url` after an HTTPS + `gg.deals` host check.
- `tiers_json` carries the selection count and the per-item URLs. Nothing is written to `game_offers`.

## Background job and options (`GgDealsBundleSyncJob`, `GgDealsBundleOptions`)

- **Off by default**: `GgDealsBundles:Enabled` defaults to `false`, so the job logs "disabled" and
  returns until an operator configures a key and enables it.
- PostgreSQL session advisory lock **`7_831_442_094`**, distinct from the wishlist sync
  `7_831_442_091`, the ntfy deals `7_831_442_092` and the global ITAD sweep `7_831_442_093`. A collision
  would make one job silently skip the other; the checks assert the separation.
- Startup delay, then a pass, then a `RefreshHours` interval plus `JitterMinutes`; the startup recovery
  pass is gated by `MinHoursBetweenRuns` through `job_runs` (`bundles.ggdeals-sync`). An incomplete sweep
  is recorded as `failed` with its reason; an interrupted pass stays `running`.
- The job logs only the exception **type**, never its message, and stores only `errorType` in `job_runs`:
  a raw provider message or URL must never leak the key.

## Catalog and UI

- `BundleCatalogService` now reads three sources (`itad`, `itad_global`, `ggdeals`). `BundleCatalogOrder`
  groups snapshots by **source family**, so `itad` + `itad_global` still collapse (existing behavior)
  while a `ggdeals` key can never be deduplicated against an ITAD bundle even if the key text matches.
- GG.deals tiers have no ITAD UUID and no linked games, so they produce **no wishlist match** and no
  per-game cheaper claim. The self-corrected rule is honored: a missing identity does **not** suppress
  the tier's average share — the denominator is `gamesCount` when it is satisfiable, otherwise the listed
  games, independent of identity.
- `BundleCatalogTierShare` and `SteamGameBundleTier` gained a trailing optional `SelectionCount`;
  `SteamGameBundleTierGame` gained a trailing optional `Url` (the item URL). All default to null, so
  every ITAD tier/item keeps its previous shape and value. This is the "share record extensions"
  reading: selection count on the tier/share record, item URL on the tier item.
- The card badge is source-specific (`GG.deals` vs `ITAD`) and a gg.deals card states the **US reference
  region** and that it does not certify MX availability or final price. The catalog already carried
  `source`, so no contract field was added for that.
- Wishlist titles are still never compared: `BundleWishlist.Match` uses only an exact tier UUID or a
  proven linked appid.

## Required wiring (parent-owned, exact)

`Deals.API/Program.cs`:

```csharp
builder.Services.Configure<GgDealsBundleOptions>(
    builder.Configuration.GetSection(GgDealsBundleOptions.SectionName));
```

`Deals.API/Extensions/ServiceCollectionExtensions.cs` (near the other bundle registrations):

```csharp
services.AddOptions<GgDealsBundleOptions>()
    .Validate(o => o.MaxPages > 0, "GgDealsBundles:MaxPages must be greater than zero.")
    .Validate(o => o.CursorValidityMinutes > 0, "GgDealsBundles:CursorValidityMinutes must be greater than zero.")
    .Validate(o => o.RefreshHours > 0, "GgDealsBundles:RefreshHours must be greater than zero.")
    .Validate(o => o.JitterMinutes >= 0, "GgDealsBundles:JitterMinutes must not be negative.")
    .Validate(o => o.StartupDelaySeconds >= 0, "GgDealsBundles:StartupDelaySeconds must not be negative.")
    .Validate(o => o.MinHoursBetweenRuns >= 0, "GgDealsBundles:MinHoursBetweenRuns must not be negative.")
    .ValidateOnStart();

services.AddScoped<IGgDealsBundleSnapshotWriter, EfGgDealsBundleSnapshotWriter>();
services.AddScoped<GgDealsBundleSyncService>();
services.AddSingleton(serviceProvider =>
{
    var job = serviceProvider.GetRequiredService<IOptions<GgDealsBundleOptions>>().Value;
    return new GgDealsBundleSyncSettings(job.MaxPages, job.CursorValidityMinutes);
});
services.AddHostedService<GgDealsBundleSyncJob>();
```

No new secret: the client reuses `GgDealsOptions` (`GgDeals:ApiKey`, `GgDeals:BaseUrl`,
`GgDeals:Region`, `GgDeals:TimeoutSeconds`) and the existing `GgDealsRequestGovernor`.

`docker-compose.yml` (optional, only if gg.deals bundles are enabled):

```yaml
      - GgDealsBundles__Enabled=${GgDealsBundles__Enabled}
      - GgDealsBundles__MaxPages=${GgDealsBundles__MaxPages}
      - GgDealsBundles__CursorValidityMinutes=${GgDealsBundles__CursorValidityMinutes}
      - GgDealsBundles__RefreshHours=${GgDealsBundles__RefreshHours}
      - GgDealsBundles__JitterMinutes=${GgDealsBundles__JitterMinutes}
      - GgDealsBundles__StartupDelaySeconds=${GgDealsBundles__StartupDelaySeconds}
      - GgDealsBundles__MinHoursBetweenRuns=${GgDealsBundles__MinHoursBetweenRuns}
```

## Self-review: S# evidence, commands and exits

| Evidence | Command | Observed |
|---|---|---|
| Backend compiles | `dotnet build Deals.sln` | Build succeeded, 0 warnings, 0 errors, exit 0 |
| New gg.deals parser/sweep/mapper | `dotnet run --project checks/GgDealsBundles.Checks/GgDealsBundles.Checks.csproj` | `PASS: gg.deals active bundle parser, sweep and mapper checks`, exit 0 |
| Existing catalog rules preserved | `dotnet run --project checks/BundleCatalog.Checks/BundleCatalog.Checks.csproj` | `PASS: bundle share allocation checks`, exit 0 |
| Existing global ITAD sweep preserved | `dotnet run --project checks/GlobalBundles.Checks/GlobalBundles.Checks.csproj` | `PASS: global bundle sweep and parser checks`, exit 0 |
| Contract + selection count | `cd Deals.Web && node --experimental-strip-types --test lib/contracts/bundles.test.ts` | 22 tests, 22 pass, 0 fail, exit 0 |
| RED probe: sweep total guard | remove the final `bundles.Count != TotalCount` branch | `FAIL: 1 ... a final count that does not match the provider total is incomplete`, exit 1 |
| RED probe: cursor guards | loosen `IsAllowedCursor` to scheme-only | 5 failures (foreign host, wrong path, userinfo, fragment, non-default port), exit 1 |
| RED probe: UI selection count | force `selectionCount: null` in `normalizeTier` | `not ok 21 - una regla elige N de M resoluble se conserva`, exit 1 |
| Whitespace hygiene | `git diff --check` | clean, exit 0 |

`pnpm` is not installed on this machine and was not installed or retried, so `pnpm build`, `pnpm lint`
and any browser check were **not** run. That is a disclosed gap, not a pass.

## Gaps and risks

- **No live fixture / no key call.** The parser follows the sourced contract and synthetic fixtures only;
  the real response shape, quota headers and cursor format remain uncertified.
- **The EF writer's raw SQL is reviewed, not executed.** There is no reachable PostgreSQL here; the
  `ON CONFLICT`, transaction and source-scoped delete were not run. In particular, EF translation of
  `CatalogSources.Contains(bundle.Source)` is compile-verified but not DB-verified.
- **`pnpm`/frontend build unavailable.** The card badge, region note and selection-count rendering are
  type-checked by the contract test only; they were not compiled by Next.js or seen in a browser.
- **UI reference region is a frontend constant.** `GGDEALS_REFERENCE_REGION = "US"` matches the provider
  default; if an operator changes `GgDeals:Region`, that label becomes stale. The backend exposes no
  region per item, so this is a known limitation.
- **`Deals.Web/app/bundles/page.tsx` still hard-codes the page-level `ITAD` badge and subtitle.** That
  file is outside this work unit's edit surface; the parent should update it to mention both sources.
- **`GgDealsBundleOptions`/`GgDealsBundleSyncJob` are not compiled by any check project** (checks
  reference `Deals.BusinessLogic` only); they are verified by `dotnet build Deals.sln` and review.
- A sustained outage or a repeated `TotalMismatch` preserves the previous snapshot indefinitely, which
  is the designed behavior but makes the degradation visible only in logs and `job_runs`.
- Advisory lock `094` now coexists with `091`/`092`/`093`; future jobs must pick a new key.

## Key Learnings

- The provider's "active" catalog paginates **only** by an opaque `next` URL. Without an id and with a
  nullable `url`, there is no vendor identity: the only honest key is a documented client heuristic,
  and it must be namespaced so it can never collide with another provider's keys.
- A cursor is a credential-bearing URL. Validating scheme/host/path/port/user-info/fragment before
  following it, and keeping every error message fixed, is what stops the API key from leaking through a
  redirect or an exception log.
- The explorer correction matters: a missing ITAD UUID removes only *identity* claims (wishlist match,
  per-game cheaper). It does **not** remove the tier average. `gamesCount` is the honest denominator when
  satisfiable, otherwise the listed games — so identity and arithmetic must stay separate concerns.
- Total accounting is not optional. Comparing the collected distinct count against the provider's
  `totalCount` (and watching it for changes between pages) is what turns "I stopped paging" into "I have
  the whole catalog".
- `off by default` is the safe posture for a new external integration: the job, the options and the
  wiring exist, but nothing spends quota or touches the network until an operator sets `Enabled=true`.
- Extending positional records with **trailing optional** members kept every existing ITAD producer and
  the persisted `tiers_json` valid without a migration; all new fields default to null.
