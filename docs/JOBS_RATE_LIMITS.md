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
| `JobBudgets__GgDeals__MaxBurstRecords` | `25` | records admitted in one request |
| `JobBudgets__GgDeals__MinDelayMilliseconds` | `200` | minimum delay between requests |

Defaults leave a 10% margin below the verified provider quotas: ITAD 1000 requests/300 seconds, and GG.deals 100 records/minute plus 1000 records/hour. These are local safety budgets, not claims about provider behavior.

`GgDealsClient` emits structured rate-limit telemetry with provider name, requested record count, and the provider's `x-ratelimit-limit`, `x-ratelimit-remaining`, and `x-ratelimit-reset` values. It never logs API keys, request URLs, or response payloads.

The daily purge deletes only finished `job_runs` older than `RetentionDays`, in bounded batches. Rows with status `running` are never deleted. Database migrations are not applied by this unit.
