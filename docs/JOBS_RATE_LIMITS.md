# Jobs outbound budgets

Configuration section: `JobBudgets` in `appsettings.json` or environment variables using the standard double-underscore form.

| Setting | Default | Unit |
|---|---:|---|
| `JobBudgets__RetentionDays` | `1` | completed-run retention days |
| `JobBudgets__PurgeBatchSize` | `500` | rows per purge batch |
| `JobBudgets__Itad__RequestsPerFiveMinutes` | `900` | ITAD HTTP requests / 5 minutes |
| `JobBudgets__Itad__MaxBurst` | `25` | requests admitted in one burst |
| `JobBudgets__Itad__MinDelayMilliseconds` | `200` | minimum delay between requests |
| `JobBudgets__GgDeals__RecordsPerMinute` | `90` | requested Steam IDs / minute |
| `JobBudgets__GgDeals__RecordsPerHour` | `900` | requested Steam IDs / hour |
| `JobBudgets__GgDeals__MaxBurstRecords` | `90` | records admitted in one request (maximum `100`, matching the client hard limit) |
| `JobBudgets__GgDeals__MinDelayMilliseconds` | `200` | minimum delay between requests |

Defaults leave a 10% margin below the verified provider quotas: ITAD 1000 requests/300 seconds, and GG.deals 100 records/minute plus 1000 records/hour. `MaxBurstRecords` may be configured up to the client's hard limit of `100`, but validation also keeps it at or below the configured minute and hour budgets. These are local safety budgets, not claims about provider behavior.

`GgDealsClient` emits structured rate-limit telemetry with provider name, requested record count, and the provider's `x-ratelimit-limit`, `x-ratelimit-remaining`, and `x-ratelimit-reset` values. It never logs API keys, request URLs, or response payloads.

The daily purge deletes only finished `job_runs` older than `RetentionDays`, in bounded batches. Rows with status `running` are never deleted. Database migrations are not applied by this unit.

## Manual runs (admin Jobs page)

`POST /api/jobs/{job}/run` is the only way to start a job by hand. It is `AdminWithId`, limited to the seven
jobs in the catalog, and rate limited by its own policy:

| Policy | Default | Partition | Rationale |
|---|---:|---|---|
| `jobs-dispatch` | 6 requests / minute | administrator id | every accepted request spends a provider budget; the duplicate guard is the queue, not this limit |

The request only records a `job_runs` row with `trigger = manual` and hands the run to a hosted consumer; it
answers `202` with the `jobRunId` instead of waiting for the work. The consumer executes one manual run at a
time per process, under the **same PostgreSQL session advisory lock as the scheduled cycle**: wishlist
`7_831_442_091`, ntfy `7_831_442_092`, ITAD bundles `7_831_442_093`, gg.deals bundles `7_831_442_094`, ITAD
deals `7_831_442_095`, FX `7_831_442_096` and purge `7_831_442_097`. FX and purge had no lock before this
unit; they have one now because a manual trigger made an overlap possible.

Consequences worth knowing:

- Manual rows use `trigger = manual`, which `JobRunLog.RanWithinAsync` excludes from the start-up gate, so a
  manual run never delays or cancels the next scheduled one.
- A `running` row older than six hours is treated as an abandoned process: it is reported as
  `stale` in the catalog and no longer blocks a new request. Until the `abandoned` status is persisted, this
  window is where the distinction lives.
- `ntfy.alerts` and `jobs.purge` are now recorded like the other five jobs, so the history is the whole host
  and not only the provider sweeps. The ntfy polling cycle (default 15 minutes) writes one row per cycle;
  raise `JobBudgets__RetentionDays` only if that history is needed for longer than a day.
- `details` is sanitised on read (`JobDetailsSanitizer`): a credential-named property never renders its
  value, `key=…`/`password=…`/bearer fragments and URL userinfo are redacted, and a string value is
  truncated to 2000 characters. The shape of the JSON is preserved.

Bounded options, per job (unknown keys are rejected, not ignored):

| Job | Option | Default | Meaning |
|---|---|---:|---|
| `wishlist.sync` | `includePriceRefresh` | `false` | also run the per-game price pass (tens of minutes) |
| `ntfy.alerts` | `produce` | `true` | fill the outbox from persisted snapshots |
| `ntfy.alerts` | `dispatch` | `true` | send due outbox rows (already-sent alerts are deduplicated) |
| `jobs.purge` | `dryRun` | `false` | count what would be deleted, delete nothing |

The purge is the only destructive job: it refuses to run without `confirm: true` unless `dryRun` is set, and
the page asks for an explicit acknowledgement first. The three sweeps and the FX refresh take no options —
their page caps and budgets are configuration, and offering them as request overrides would be an arbitrary
configuration change, not a bounded option.
