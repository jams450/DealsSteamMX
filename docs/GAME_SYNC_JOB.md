# Staged game synchronization

## Schedule and phases

Declared defaults (`WishlistOptions`), not runtime verification: enabled, daily at
03:00 `America/Mexico_City`, jitter up to 10 minutes; startup delay 60 seconds.
Startup recovery is gated by `MinHoursBetweenRuns` (20); daily slot is not.
Existing PostgreSQL job lock remains unchanged.

T1 completes Steam pass before due external snapshots. Provider-only work uses
per-provider freshness and ITAD identity prerequisites; GET remains Steam-only,
explicit full refresh remains full. Failed providers preserve old snapshots and
timestamps. Job per-game pacing (`MaxRefreshesPerHour`, default 1000) is separate
from HTTP admission: a game can require multiple requests or none.

## Direct-store HTTP admission

`JobBudgets:Epic` and `JobBudgets:Microsoft` each default to:

| Setting | Default |
| --- | ---: |
| `RequestsPerMinute` | 30 |
| `RequestsPerHour` | 600 |
| `MinDelayMilliseconds` | 1000 |

These are cautious **local policies, not verified external quotas**. Positive
request limits and nonnegative delay are validated at startup. Each typed singleton
has independent rolling minute/hour windows and concurrency 1. Foreground and
background share that provider's admission state. Every outbound GET/POST attempt
uses one admission (lookup/search/pricing), not one unit per game. An admitted
transport failure still consumes budget; a cancelled admission wait does not.
ITAD/GG.deals budgets and implementation remain unchanged.

429 sets shared provider cooldown from typed `Retry-After` delta or HTTP date.
Missing/malformed delay falls back to 60 seconds. Valid server delay is never
capped downward; later short cooldowns cannot shorten an existing deadline.
Past dates impose no additional future wait. No immediate retry or network loop;
response stays caller-owned and existing typed failure handling remains intact.
Cancelled waits release the concurrency gate; lease disposal is idempotent.

## Limits and evidence

Governors are **per process, not distributed**. PostgreSQL job locking does not
coordinate foreground HTTP admission across replicas; summed traffic can exceed
one process's configured policy. Restart resets windows/cooldown. No distributed
redesign, external quota certification, live provider calls or deployment here.
Job provider counters infer phase attempts/results from plans and snapshot
changes; they are **not measured HTTP request counts**. Governor admission is the
actual HTTP-attempt boundary, not new persisted HTTP telemetry.

`checks/GameSync.Checks` exercises real governor classes without HTTP: same-provider
serialization, cross-provider isolation, rolling windows, minimum delay,
cancellation recovery, idempotent leases and 429 delta/date/fallback deadlines.
Short windows are harness-only constructor overrides; production uses minute/hour
windows. Fake clock checks deadlines by advancing before a new acquisition; it does not
wake an acquisition already waiting. `Task.Delay` uses real cancellable time,
not the injected clock. Blocked acquisitions are cancelled in the harness before
clock advancement; no live fake-timer wakeup coverage or long real cooldown waits.
Malformed `Retry-After` added with `TryAddWithoutValidation` is exercised through
the typed header getter and falls back to 60 seconds without throwing.
