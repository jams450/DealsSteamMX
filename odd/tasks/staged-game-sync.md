# Staged wishlist game synchronization

## Objective
Same daily job completes Steam pass first, then updates due ITAD, Microsoft, Epic and gg.deals snapshots. Preserve GET Steam-only and explicit interactive refresh behavior.

## Constraints
No secrets, schema changes, job execution or deployment; preserve existing dirty changes. No commits requested. Provider failures must not stop other stores; cancellation persists completed progress. ITAD identity precedes direct stores. Per-provider timestamps determine due work; Steam not requested twice. ITAD/GG existing governors kept. MS/Epic safety budgets configurable, not advertised as official quotas.

## Tasks
- [x] T1 (done): Steam pass then provider-only pass integrated; policy RED stub then GREEN; independent build/checks passed and readback confirms timestamp failures preserved and phase checkpoint. Harness is policy-only, no actual DB/job execution.
- [x] T2 (done): Independent typed Epic/MS governors (local30/min600/h1s), rolling budgets, sem1 and429RetryAfter/fallback60s. RED missing typed governor API then GREEN harness; writer build0warnings/errors. Documentation added.
- [x] T3 (done): Independent console checks and dotnet build PASS (0 warnings/errors). Readback verifies two phases, failure timestamp gates, typed governors and DI. Review inspect performed; scope includes unrelated tracked edits, no START. Actual job/DB/provider HTTP execution untested.

## Acceptance
All Steam work finishes before first external call. External pass skips fresh timestamps and preserves failed provider data/timestamps. GET unchanged; interactive full refresh unchanged. Store calls budgeted per HTTP request, independent governors and cancellation-aware waits. No repeated Steam HTTP in provider pass. Job logging truthful; no cache result mislabeled as actual provider attempt.

## Evidence
WishlistSyncService:194 calls forceRefresh false; SteamGameService:540 gates external phases on forceRefresh, preventing scheduled store refresh. Existing ItadRequestGovernor and GgDealsRequestGovernor use windows; MS/Epic share semaphore-only ProviderRequestGovernor. Defaults schedule 03:00 Mexico City + jitter 10min unchanged.

## Next step
Deploy only when requested; observe next scheduled job logs/provider stamps under real configuration. Schedule unchanged; no commit or live job run performed. Runtime quotas MS/Epic remain unverified; governors are per-process.
