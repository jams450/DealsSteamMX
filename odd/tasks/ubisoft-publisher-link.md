# Ubisoft search link gated by Steam publisher

## Objective
Show the existing Ubisoft search link on game detail only when Steam appdetails lists Ubisoft as a publisher. Retrieve the publisher metadata from Steam, not inferred ownership, title or developer.

## Scope and constraints
- Ubisoft only; other store links and LATAM scraping are out of scope.
- Unknown, missing or non-Ubisoft publisher hides the link.
- Preserve existing uncommitted changes, especially AGENTS.md and game-client.tsx.
- Extend the existing metadata/API contract; avoid an extra frontend metadata request.
- Persist nullable publisher names as PostgreSQL text[] when native Npgsql mapping supports it; unknown/old rows hide the link until normal expiry or forced refresh. Do not make publisher presence a cache-validity requirement.
- Database changes, if needed, are manual migration artifacts only. Do not apply on server.
- No secrets, server changes, publishing or commits without explicit user request.

## Tasks
- [x] T1 (done): implemented Steam publisher propagation and conditional Ubisoft link; 14 focused tests passed after observed RED/GREEN. Nullable text[] storage keeps cached legacy rows hidden until normal/forced refresh.
- [x] T2 (done): independent verification passed: 14 focused tests, backend build (0 warnings/errors), frontend production build (41 pages) and git diff --check. Native ordinary review approved and exact acknowledgement completed.

## Acceptance and checks
- Ubisoft publisher shows the existing safe external search link.
- Another publisher, missing metadata, malformed data and developer-only Ubisoft do not enable it.
- Frontend contract remains tolerant of older API responses.
- Relevant deterministic tests observe RED then GREEN; backend and frontend builds pass or blockers are recorded.
- New schema fields match entity mappings and manual migration.

## Evidence
- Exploration delegated to gentle-ai-explore: publishers are currently absent from persisted Steam metadata and API detail.
- Read-only verifier confirmed price/free snapshot gates must remain unchanged to avoid retry loops; publisher eligibility is independent of price comparability.
- Runtime tools available: dotnet, node, pnpm. Native RDD switch is on.
- Existing working tree is dirty; main HEAD 6dd415e. No work-unit commits authorized.

## Verification outcome
- Independent verifier: node --experimental-strip-types with explicit Ubisoft test file ran 3 tests; node --experimental-strip-types --test lib/contracts/steam.test.ts ran 11 tests; 14 passed total. Node --test can misinterpret the [steamAppId] path and discover 0 tests; execute the file explicitly.
- dotnet build Deals.sln: passed, 0 warnings/errors.
- Deals.Web pnpm run build: passed, 41 generated pages; existing img warnings remain.
- git diff --check: passed.
- Native ordinary review review-6cf639203c941523: approved and acknowledged; authority burned for frozen candidate. Final task-document status is bookkeeping after that review, not a source correction.
- Native ASSESS remained unassessable because separate untracked declaration was required; independent high-risk verification completed.
- No backend parser unit tests, browser/API/DB smoke or live migration execution. No commits or deployment performed.
- Exact publisher matching intentionally excludes longer names such as Ubisoft Entertainment; no speculative aliases added.

## Next step
Apply SQL/migrations/2026-10-09_steam_game_publishers.sql before deploying the API/frontend. Then use the existing Actualizar ofertas action on an old cached game to obtain publishers and check Ubisoft/non-Ubisoft visibility in the browser. Deployment and live verification remain operator actions.
