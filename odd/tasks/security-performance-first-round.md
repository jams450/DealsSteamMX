# First round: security and performance fixes

## Objective
Fix the seven findings the user approved from the security and optimization audit of every module
(backend and frontend), in the order reported, on branch `fix/first-round-security-and-performance`.
Two are remotely exploitable today (login lockout bypass, shared auth rate-limit partition); the rest
are availability, secret-handling and dead-feature defects.

## Constraints
- Read-only audit is closed: `main` is clean of source edits; every change lands on this branch.
- Nothing of the audit's "verified absent" list may regress: no SQL injection, `AsNoTracking` on reads,
  money as integer minor units, `UtcNow` only.
- Do not weaken any existing control to fix another one. In particular, `ForwardedHeaders` must never
  trust an unconfigured proxy chain (that would turn the limiter into an IP-spoofing bypass).
- No production values, no real `.env`/`appsettings.json` reads, no schema changes on a live server.
- Commits: one work unit per task on this branch. No push, no PR, no merge.

## Specs
- **S1** `AuthService.cs:104` compares the raw submitted identifier against `users.email`, while
  `UserService.cs:341` normalizes with `Trim().ToLowerInvariant()`. Submitted spelling variants must
  resolve to the same account for both the lock check and the failure counter, so the account lockout
  cannot be evaded by changing case or adding surrounding whitespace.
- **S2** `RateLimitingExtensions.cs:41` partitions by `Connection.RemoteIpAddress` and no
  `ForwardedHeaders` middleware exists in the repository, so every proxied login and refresh shares one
  10/min bucket. A login flood must not consume the refresh budget, and a real proxy chain must be
  trustworthy only when explicitly configured.
- **S3** `Repository.cs:20` calls `_context.Database.SetCommandTimeout(0)`, an infinite timeout that
  covers unbounded `pg_advisory_xact_lock` and `FOR UPDATE` waits, so a stuck lock pins a pooled
  connection forever. Command timeouts must be finite and configurable, and lock waits must fail fast
  with a clear error instead of hanging.
- **S4** `ItadClient.cs:815` applies the provider's `Retry-After` verbatim (`:1162` builds it), while
  `GgDealsClient.cs:659` caps its own with `Math.Min(seconds, MaxRetryWaitSeconds)`. A provider-supplied
  wait must be bounded in both ITAD clients.
- **S5** `.env.example:7` carries `Jwt__Key=replace-with-a-long-random-secret` (33 characters, no `SET_`
  prefix) and `AuthenticationExtensions.cs:85` accepts it, so a known signing key would boot. Placeholder
  secrets must be rejected anywhere a secret is validated: `Jwt:Key`, `SESSION_SECRET`, the startup
  admin password.
- **S6** `LibraryController.cs:552` (`RequireText`) rejects only blank, but `user_library.title` is
  `VARCHAR(256)` and `store_game_id` is `VARCHAR(64)`. One oversized entry must be rejected as 400
  before the loop, never as a 500 that rolls back the whole import.
- **S7** `LibraryController.cs:380` declares `[HttpDelete("library/{userLibraryId:long}")]` under
  `[Route("api/[controller]")]` (real path `api/library/library/{id}`), while the only caller is
  `Deals.Web/app/api/bff/library/manual/[id]/route.ts:29` (`api/library/{id}`). The manual-add undo must
  work; the canonical path is the short one the caller already uses.

## Tasks
- [x] T1 S1 Route: inline (paired files, one shared helper). Commit `d1290cc`. Evidence: `checks/Auth.Checks` failed on both S1 rules before the change (control case passing) and passes after.
- [x] T2 S2 Route: inline (backend only). Commit `aa8a532`. Evidence: new trusted-proxy checks in `checks/Auth.Checks`; six pre-existing suites still pass.
- [x] T3 S3 Route: inline (five call sites, one helper). Commit `3e1dbb3`. Evidence: `checks/IdentityLock.Checks` pins statement order, the unchanged lock key, the `55P03` classification and the non-lock failure path.
- [x] T4 S4 Route: inline (two clients, one shared reader). Commit `c48a71d`. Evidence: `checks/GlobalDeals.Checks` asserts a day-long `Retry-After` still retries once without parking the caller.
- [x] T5 S5 Route: inline (shared guard + TypeScript mirror). Commit `9b89e55`. Evidence: `checks/Auth.Checks` and `checks/secret-quality` assert the same cases on both sides.
- [x] T6 S6 Route: inline (shared limits + controller). Commit `4169b03`. Evidence: `checks/IdentityLock.Checks` reads `SQL/schema.sql` and asserts the three column widths equal the limits.
- [x] T7 S7 Route: inline (one route attribute). Commit `e44237f`. Evidence: route composition checked at both ends; no other caller existed.

## Log
- **L1** (user, verbatim) `Necesito review de seguridad y optimizacion para front y back de cada modulo que tenemos`
- **L2** (user, verbatim, authorizing this round) `empieza`
- **L3** Audit evidence (read-only, verified by the parent, not inferred): login lockout bypass at
  `AuthService.cs:104` + `UserService.cs:341` + `:159-161`; shared limiter bucket at
  `RateLimitingExtensions.cs:41` with 0 `ForwardedHeaders` matches; infinite `SetCommandTimeout(0)` at
  `Repository.cs:20`; uncapped ITAD `Retry-After` at `ItadClient.cs:815,1162` vs the capped gg.deals twin
  at `GgDealsClient.cs:659`; `.env.example:7` placeholder accepted by `AuthenticationExtensions.cs:85`;
  `RequireText` at `LibraryController.cs:552` against `SQL/schema.sql:50,55`; duplicated route segment at
  `LibraryController.cs:380` vs `bff/library/manual/[id]/route.ts:29`.
- **L4** Baseline before any edit: `dotnet build Deals.sln` 0 errors/0 warnings; `pnpm build` OK;
  6/6 `checks/` suites PASS exit 0.
- **L5** Two verification projects were added, both DB-free: `checks/Auth.Checks` (in-memory provider,
  because this checkout has no reachable PostgreSQL and the rule lives in `AuthService`, which needs a
  `DbContext`) and `checks/IdentityLock.Checks` (a recording repository; the `PostgresException` for
  `55P03` turned out to be constructible, so the conversion is asserted rather than only classified).
- **L6** A check found a defect in this work: the placeholder rule knew `change_me`/`changeme` but not
  `change-me`, and both the C# and the TypeScript sides failed the same case. The rule now drops `-`, `_`
  and space before matching, and both sides assert the hyphen and space spellings. This is the reason the
  two sides assert the same cases: a mirrored rule that is not cross-checked drifts.
- **L7** Scope deviation, deliberate: T5's shared guard also replaced the four existing
  `StartsWith("SET_")` checks (ntfy, ITAD, gg.deals, Banxico). They each carried a private copy of the
  same rule, which is the duplication that let the `Jwt:Key` guard diverge from its own documented
  convention. The change is strictly stricter and fails closed at startup.
- **L8** Blocked step: `.env.example` cannot be edited from this session (harness safety policy on
  sensitive paths, and circumventing it with another tool was not an option). It still lacks the two
  commented `Proxy__*` lines for T2; `AGENTS.md` carries the same options.
- **L9** Deliberate behaviour changes that a deployment will notice: a placeholder `Jwt:Key`, `SESSION_SECRET`
  or bootstrap administrator password now refuses to start (T5); an identity transaction that waits more
  than 15 s for the gate now answers 409 instead of waiting (T3); commands have a 60 s timeout unless
  `Database__CommandTimeoutSeconds` changes it (T3).
- **L10** Independent verification (`gentle-ai-verify` over `afeb106..ca80a13`) returned S1, S4, S5, S6 and
  S7 verified and S2 and S3 partially verified, and its verdict was "not safe to keep as-is". What it found,
  and what was done about it: (a) **blocker, now fixed** - `Proxy:KnownProxies` read as a list returns null
  for the comma-separated leaf that `AGENTS.md` documents, so enabling the feature failed at startup
  claiming no proxy was named; `TrustedProxySettings.FromConfiguration` now reads both shapes and the check
  builds real configuration for each. (b) **false claim, now fixed** - the `IdentityLock.Checks` banner
  advertised "command timeout checks" while nothing asserted the timeout; the timeout decision is now a
  resolver with its own check and the banner was corrected. (c) **regression introduced by T3, now fixed** -
  `SET LOCAL lock_timeout` lasted the whole identity transaction, so a later row-lock wait beyond 15 s turned
  into a 500 instead of waiting; the budget is now released after the gate. (d) **documentation drift, now
  fixed** - `AGENTS.md` still described the abolished `auth` policy. (e) **coverage gap, now closed** - S2's policy split is asserted against the real
  `RateLimiterOptions` policy map (read by reflection: it is not part of the public surface, and an
  unreachable map fails the check instead of passing green).
- **L11** Provenance of the observed RED: the T1 and T4 RED runs happened in this session against the working
  tree (the check projects did not exist at `afeb106`), so they cannot be re-derived from the commit graph.
  The GREEN results can: every suite is reproducible from `ca80a13` with the commands in this document.
  Stated plainly rather than left as an implicit claim.
