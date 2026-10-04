# Auth refresh session

## Objective
Keep every application route authenticated, retain the 2-hour access token and configure 90-day refresh sessions. Diagnose and fix code-supported forced-login failures without weakening backend rotation.

## Constraints
- No secrets, real .env or appsettings.json reads; no production changes.
- Preserve unrelated dirty files. No commits without explicit user request.
- One writer. Existing global authentication stays intact.
- Local checks do not certify production.

## Tasks
- [x] T1 Diagnose refresh failure paths and document evidence. Explorer confirmed independent parallel refresh with strict backend rotation; server components do not refresh.
- [x] T2 Implement bounded concurrent-renewal mitigation, preserve proxy cookie on JSON parse failure, configure Compose durations, and document limits. Worker observed 5/5 tests passing; runtime integration and residual error paths remain unverified.
- [x] T3 Run focused tests and frontend/backend builds and independent verification. 5/5 tests, frontend build and backend build passed.
- [x] T4 Create user-requested auth-only commit on fix/auth-refresh-session. Commit message: fix(auth): coordinate refresh rotation and configure session lifetime. Initial work-unit identity: 9ec77f6 (amended only to record completion; final identity in Git).
- [ ] T5 Validate production renewal and decide residual error-path hardening. Pending deployment by operator; no production validation yet.

## Acceptance
- Parallel expired-token requests cannot invalidate each other through duplicate rotation in the supported deployment.
- Cookie rotation reaches the browser on success and relevant error responses.
- Existing authentication/CSRF controls retained; no retry loops or secret logging.
- JWT defaults to 2 hours and refresh to 90 days in Compose; env override supported.
- Tests establish observed RED/GREEN where feasible; report skipped runtime checks.

## Evidence
Initial audit: global middleware already protects application routes; Steam search requires UserWithId. JWT default 2h; backend refresh default 30d. Compose does not pass either duration. BFF currently refreshes independently per request; backend atomically revokes old session on rotation.

## Progress
T1 complete, delegated read-only audit. T2 delegated implementation; single-process coordination only. Read-only verifier confirmed proxy parse errors lose rotated cookie; narrow proxy surface approved by parent. Direct users route error-path loss remains a documented potential follow-up, not full caller rewrite. Production values and failure logs unavailable. Commit evidence: not requested, no commits planned.

## Next step
Independent verifier passed focused tests (5/5) and frontend build (existing warnings); backend build passed with 0 warnings/errors. Assessment unassessable due untracked selection, independent verifier completed. Native inspect includes unrelated preexisting tracked changes; native START not executed to avoid broadening candidate scope. No receipt or production certification. Residuals: direct users JSON parse and post-rotation retry exceptions can lose cookie; upstream refresh 5xx still triggers old rejection behavior. Production causality not verified.
