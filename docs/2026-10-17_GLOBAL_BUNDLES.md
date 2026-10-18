# Global ITAD bundles (sweep + snapshot)

Scope: implement S2-S3 of `odd/tasks/global-bundles.md` — every active MX ITAD bundle, catalogued by a
bounded paginated sweep, persisted under an isolated `itad_global` source so it can be read back with a
wishlist highlight. This document covers the provider client, the sweep/snapshot decision and the
background job. The authenticated read/UI and the wishlist match are a separate work unit (T3).

Nothing here is certified against a live provider: no real key is used, no real send happens, and the
sample below is **synthetic**. The plan's contract facts (root array, `offset`/`limit <= 50`,
`country`/`mature`/`expired`) come from `docs/PLAN_BUNDLES.md` and the explorer note L3.

## Provider contract

```http
GET https://api.isthereanydeal.com/bundles/v1?country=MX&mature=true&expired=false&offset=0&limit=50
ITAD-API-Key: <key>
```

- The root is a **flat JSON array** of bundle objects with the same shape as `games/overview/v2`'s
  `bundles[]`: `id`, `title`, `page` (`id`/`name`/`shopId`), `url`, `details`, `isMature`, `publish`,
  `expiry`, `note`, `counts`, `tiers[].games[]` (item `id` is a UUID).
- `limit` is capped at 50 by the provider; a larger value is clamped client-side before the request.
- The API key travels only in the `ITAD-API-Key` header, never in the URL. The request is admitted by the
  shared `ItadRequestGovernor` and a `429` is retried once honoring `Retry-After`, exactly like the
  existing POST paths.
- There is **no queried-id set**. Unlike `GetBundlesAsync`, membership is not filtered: every observed
  item UUID is reported (`MatchedItadIds`) so the read side can match a wishlist UUID locally.

### Synthetic sample (schema-shaped, not a recorded response)

```json
[
  {
    "id": "11111111-1111-1111-1111-111111111111",
    "title": "Synthetic Bundle",
    "page": { "id": "fanatical", "name": "Fanatical", "shopId": 61 },
    "url": "https://isthereanydeal.com/bundle/synthetic?ref=synthetic",
    "details": "https://isthereanydeal.com/bundle/synthetic",
    "isMature": false,
    "publish": "2026-10-01T00:00:00Z",
    "expiry": null,
    "note": null,
    "counts": { "games": 2, "media": 0 },
    "tiers": [
      {
        "price": { "amountInt": 1999, "currency": "USD" },
        "addon": false,
        "games": [
          { "id": "22222222-2222-2222-2222-222222222222", "slug": "a", "title": "Game A", "type": "game", "mature": false }
        ]
      }
    ]
  }
]
```

This fixture is invented for the checks. Do not treat its ids, titles or figures as observed data.

## Parsing rules

- The envelope must be an array. A non-array root throws; it is never read as "no bundles".
- Structural violations on a bundle entry throw in the global path: a non-object entry, a missing or
  oversized `id`/`title`, a missing/invalid non-HTTPS `url`, or an unreadable `expiry`. This is stronger
  than the per-game path on purpose: the per-game parser still silently drops an unusable bundle, while a
  partial global page must never be certified as a complete catalog and purge the previous one.
- A genuinely expired bundle is dropped (it is no longer active).
- Sanitization reuses the existing mapping: control characters stripped, lengths bounded, tier prices and
  item UUIDs preserved through `BundleTierMapper`, item lists capped and marked incomplete when truncated.
  A truncated tier scan fails the page in the global path.

## Sweep completion (`GlobalBundleSweep`)

The pure accumulator decides whether the pages fetched so far form an authoritative snapshot:

| Case | Result |
|---|---|
| Page returns fewer items than the limit (including 0) | Complete |
| Page returns exactly `limit` items | Another page must be fetched |
| A page's key set repeats an earlier page | `RepeatedPage` (incomplete; provider ignored offset) |
| Full page at `MaxPages` | `PageBudgetExhausted` (incomplete) |
| A page request or parse throws | `PageFailure` (incomplete) |

Only `Complete` reaches the writer. Every other outcome returns a preserved result: **no write, no
advanced timestamp, previous snapshot stands**.

## Snapshot persistence (`EfGlobalBundleSnapshotWriter`)

- Source is the dedicated literal **`itad_global`**. The existing per-game orphan purge is scoped to
  `source = 'itad'`, so this sweep can never be deleted by a game refresh and never deletes per-game
  bundles.
- One transaction replaces the whole source: each bundle is upserted with
  `INSERT ... ON CONFLICT (source, bundle_key) DO UPDATE`, then rows of the same source not returned are
  deleted. Offers, per-game bundles and other sources are untouched.
- Provider HTTP happens **before** the transaction. The transaction only writes the already-parsed,
  already-sanitized snapshot in memory.
- `tiers_json` carries each item's ITAD UUID (via `BundleTierMapper`) so T3 can prove wishlist membership
  by exact UUID, never by title.
- `external_bundles.source` is `VARCHAR(16)`; `itad_global` (11 chars) needs **no migration** and no new
  column.

## Background job (`GlobalBundleSyncJob`)

- Enabled by default; `GlobalBundles:Enabled=false` stops it.
- Startup delay, then a pass, then a scheduled interval of `RefreshHours` plus random `JitterMinutes`.
- A PostgreSQL session advisory lock (key `7831442093`, distinct from the wishlist sync `7831442091` and
  the ntfy deals `7831442092`) serializes the cycle across instances. A run that cannot take the lock is
  skipped, not queued. Sharing a key with another job would make one of them silently skip every cycle.
- The startup (recovery) pass is gated by `MinHoursBetweenRuns` through `job_runs`; the scheduled slot is
  never gated.
- Each cycle is recorded in `job_runs` under `bundles.global-sync`. A preserved (incomplete) sweep is
  recorded as `failed` with its reason so the degradation is visible; an interrupted pass stays `running`.

## Required wiring (parent-owned)

`ServiceCollectionExtensions` must add:

```csharp
services.AddOptions<GlobalBundleOptions>()
    .Validate(o => o.PageSize is > 0 and <= GlobalBundleOptions.MaxPageSize,
        "GlobalBundles:PageSize must be between 1 and 50.")
    .Validate(o => o.MaxPages > 0, "GlobalBundles:MaxPages must be greater than zero.")
    .Validate(o => o.RefreshHours > 0, "GlobalBundles:RefreshHours must be greater than zero.")
    .Validate(o => o.JitterMinutes >= 0, "GlobalBundles:JitterMinutes must not be negative.")
    .Validate(o => o.StartupDelaySeconds >= 0, "GlobalBundles:StartupDelaySeconds must not be negative.")
    .Validate(o => o.MinHoursBetweenRuns >= 0, "GlobalBundles:MinHoursBetweenRuns must not be negative.")
    .ValidateOnStart();

services.AddSingleton(serviceProvider =>
{
    var global = serviceProvider.GetRequiredService<IOptions<GlobalBundleOptions>>().Value;
    return new GlobalBundleSyncSettings(global.PageSize, global.MaxPages);
});

services.AddScoped<IGlobalBundleSnapshotWriter, EfGlobalBundleSnapshotWriter>();
services.AddScoped<GlobalBundleSyncService>();
services.AddHostedService<GlobalBundleSyncJob>();
```

Configuration (env or appsettings):
`GlobalBundles__Enabled`, `GlobalBundles__PageSize`, `GlobalBundles__MaxPages`,
`GlobalBundles__RefreshHours`, `GlobalBundles__JitterMinutes`, `GlobalBundles__StartupDelaySeconds`,
`GlobalBundles__MinHoursBetweenRuns`.

No new secret is required: the sweep reuses `ItadOptions` (`ITAD:ApiKey`, `ITAD:BaseUrl`,
`ITAD:Country`, `ITAD:TimeoutSeconds`).

## Gaps and risks

- No live fixture exists for `bundles/v1`; the parser follows the plan's contract and the synthetic
  fixture only. Runtime certification is still pending.
- The EF writer's raw SQL (`ON CONFLICT`, transaction, source-scoped delete) is not executed by the
  checks: there is no live database here. The deterministic checks cover the pure completion rule, the
  public parser and the apply-only-on-complete decision; the writer's SQL shape is reviewed, not run.
- Blocking, sustained `429`/`5xx` on every page means the snapshot is preserved indefinitely. That is the
  designed behavior, but it also means a provider outage is invisible except in logs and `job_runs`.
- `MatchedItadIds` is not persisted as a column; the read side must derive membership from `tiers_json`.

## Key Learnings

- The per-game parser (`games/overview/v2`) and the global catalog (`bundles/v1`) share the bundle mapper
  but not the failure policy: overview drops what it cannot show, the global sweep must fail loudly so a
  partial page never purges the snapshot. The `strict` flag carries exactly that difference.
- A dedicated `source` literal is the whole isolation mechanism. Reusing `itad` would have let the
  existing orphan purge delete the global catalog on the next game refresh.
- "Exact page boundary" is the trap in paginated sweeps: a page of exactly `limit` is not the end, so an
  exact-50 catalog needs one more (empty) page before it may be certified complete.
- Keeping the completion rule pure (no I/O) is what makes the sweep testable at all in this repository,
  where there are no test projects and no live database for checks.
- Session advisory lock keys are a shared global namespace. Two background jobs that reuse a key do not
  crash: one simply skips every cycle while the other holds it. The key now lives on the service as a
  public constant precisely so the checks can assert it against the other known keys (`7_831_442_091`,
  `7_831_442_092`).
