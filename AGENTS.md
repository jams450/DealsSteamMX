# AGENTS.md

Operational map for this repo. Read this before changing code.

## What this is
Reusable full-stack template. Backend: .NET 9 API (`Deals.API`, `Deals.BusinessLogic`, `Deals.Models`). Frontend: Next.js 15 App Router app that acts as a BFF (`Deals.Web`). Database: PostgreSQL via Npgsql EF Core. The expense domain was deleted on purpose; the admin **Users** feature is the reference vertical slice (backend, BFF, and UI).

## Current phase
The current phase is the **price comparator**: direct Steam price + ITAD official/authorized offers + gg.deals retail/keyshops aggregate + FX conversion to MXN (`docs/PLAN_BASE_MVP.md`, `docs/PLAN_ITAD.md`, `docs/PLAN_GGDEALS.md`). Multi-store direct pricing (Epic now; Microsoft/Xbox next; no IGDB, ITAD demoted to identity resolver + fallback and always `pricing_type='fx_estimate'`) lives in `docs/PLAN_MULTISTORE.md`: Fases 0 and 1 are implemented, including `IStorePriceProvider`/`EpicStoreClient`, the `itad.link` redirect resolver and `steam_games.epic_refreshed_at`. Its two migrations (`2026-10-02_game_offers_canonical_anchor.sql`, `2026-10-03_epic_store_offers.sql`) are **not applied yet** — apply by hand, then run `SQL/checks/game_offers_anchor.sql` (expect 0 rows). Epic's GraphQL endpoint answers **403 without a User-Agent** (`Epic:UserAgent`) — necessary but not sufficient: that endpoint's Cloudflare WAF also classifies clients by TLS fingerprint, and OpenSSL 3.0.x gets a challenge while ≥3.3 passes. That is why the runtime stage of `Dockerfile.api` is `aspnet:9.0-azurelinux3.0` and not `aspnet:9.0`. Do **not** measure this from the host with `curl` (`docs/PLAN_MULTISTORE.md` §2.1); measure inside the runtime image. It is implemented and pending commit/deploy/runtime validation; the checkout has wide uncommitted changes. External bundles have their own V1 **display-only** implementation on the same game detail (`docs/PLAN_BUNDLES.md`): ITAD `games/overview/v2` discovery + `external_bundles`/`external_bundle_games`. Bundles never enter `game_offers`, `selectBestPrice` or any savings math. Savings calculation is still not implemented, and no live MX fixture has certified the parser yet — treat the ITAD bundle contract as pending runtime validation. The Playnite library is implemented with per-game favorites (`user_game_favorites`), Steam cover art filled into `games.image_url` (`POST /api/library/covers/sync`, bounded pass; `PUT /api/games/{gameId}/cover` for a manual pick — URL only, never identity or prices) and multiple reviews per `(game, platform)` carrying a mandatory `status` (`finished | completed | dropped`; "por jugar" is the absence of a review). Future plans live in `docs/`: canonical games (`PLAN_CATALOG.md`), Playnite library/reviews (`PLAN_LIBRARY.md`), wishlist (`PLAN_WISHLIST.md`), and the verified export procedure (`PLAYNITE_EXPORT.md`). `docs/dealext-library.json` is a personal import fixture and must remain ignored.

## Reality check
- `Deals.sln` contains only the 3 backend projects. `Deals.Web` is a separate pnpm project, not in the solution.
- No EF migrations workflow. `SQL/schema.sql` is the schema source of truth, `SQL/migrations/` holds dated manual migrations.
- `SQL/checks/*.sql` holds read-only invariants (every query must return 0 rows). Run them after any change that creates or reassigns canonical identity (library import, game merge).
- No test projects exist in this repo.
- Everything targets `net9.0`. Do not bump to .NET 10.
- The old expense/Gastos naming is gone. Do not reintroduce it.

## Entrypoints
- API composition root: `Deals.API/Program.cs`, fluent setup in `Deals.API/Extensions/*.cs`.
- Controllers: `Deals.API/Controllers/` (`AuthController`, `MeController`, `UsersController`).
- Business services: `Deals.BusinessLogic/Services/` (`Repository`, `UserService`, `PasswordService`).
- Entities: `Deals.Models/Entities/` (base audit fields in `Deals.Models/Models/BaseModel.cs`).
- DbContext and mappings: `Deals.BusinessLogic/Context/AppDbContext.cs`.
- Schema: `SQL/schema.sql`; auth migration: `SQL/migrations/2026-05-07_auth_refresh_sessions_and_lockout.sql`.
- Frontend session/BFF/security: `Deals.Web/lib/auth/*`, `Deals.Web/lib/bff/*`, `Deals.Web/lib/security/*`, `Deals.Web/middleware.ts`.
- Reference slice: `Deals.Web/app/users/*`, `Deals.Web/app/api/bff/users/*`, `Deals.Web/lib/contracts/users-admin.ts`.
- UI contract: `Deals.Web/DESIGN.md`, `Deals.Web/THEME_COLORS.md`.

## Stack
- .NET 9: `Deals.API` (Web SDK), `Deals.BusinessLogic`, `Deals.Models`. Npgsql EF Core 9, Mapster 7.4, JwtBearer 9.
- Next.js 15.5 App Router, React 19, TypeScript, Tailwind v4 (CSS-first, no `tailwind.config.*`), `jose`, `@tanstack/react-table`.
- PostgreSQL.

## Verify commands
Backend:
```bash
dotnet build Deals.sln
dotnet run --project Deals.API/Deals.API.csproj   # dev URLs: http://localhost:5181 , https://localhost:7052
```
Frontend (`node` and `pnpm` exist here; `npm`/`npx` do NOT):
```bash
cd Deals.Web
pnpm install --no-frozen-lockfile
pnpm build
pnpm dev   # http://localhost:3000
node .next/standalone/server.js   # la build de producción como la corre la imagen (pnpm start avisa: output standalone)
```
Both from the repo root (reads `.env`):
```bash
pnpm dev
```
`Deals.Web/Dockerfile` mirrors the local toolchain: `node:24-alpine` with pnpm pinned to the same version this machine uses (`pnpm@11.26.0`), installed globally with `npm i -g` (npm exists inside the image even though it is not on this machine). It uses `pnpm install --frozen-lockfile`, so a lockfile out of sync fails the build instead of resolving new versions. The runtime stage has no pnpm and no `node_modules`: `next.config.ts` sets `output: "standalone"` and the container runs `node server.js` as the unprivileged `node` user. `Deals.Web/.dockerignore` keeps the host's `node_modules`, `.next` and `.env` out of the context.

## Auth and security model
Backend:
- Access token from `JwtOptions` (`Jwt` section, `ExpirationHours` default 2). Claims: `sub` and `NameIdentifier`, `Name`, `sessionVersion`, `sid`, `jti`, plus `Role=Admin` when the user is admin.
- `AuthenticationExtensions` validates the token, then hits the DB on every request: user must be active, not locked, `SessionVersion` must match the token, and `sid` must be a live unexpired, non-revoked session. `ClockSkew` is zero.
- `AuthService` does refresh-token rotation: SHA-256 hashed tokens in `user_sessions`, atomic revoke-and-replace with `ReplacedBySessionId`, TTL from `Auth:RefreshDays`. Login lockout: `Auth:MaxFailedAttempts` then `Auth:LockMinutes`.
- `PasswordService` hashes with PBKDF2-SHA256, 120k iterations, format `pbkdf2$v1$iterations$salt$hash`.
- Policies in `AuthorizationExtensions`: `UserWithId` (authenticated + valid id claim), `AdminWithId` (+ `Role=Admin`).
- `UsersController` uses `AdminWithId`. `MeController` uses `UserWithId` and reads claims itself, throwing `UnauthorizedAccessException` when the id is missing. `CurrentUserService` (`ICurrentUserService`) exposes nullable `GetUserId()`; `GetRequiredUserId()` throws `InvalidOperationException`.
- `ExceptionHandler` maps `ArgumentException` to 400, `UnauthorizedAccessException` to 401, everything else to 500 (ProblemDetails, Spanish titles).
- Public endpoints: `POST /api/auth/login` and `/refresh`; `POST /api/auth/logout` requires `UserWithId`. Login and refresh are rate-limited by the `auth` policy: fixed window 10/min per IP (`RateLimitingExtensions` + `[EnableRateLimiting("auth")]`). User creation is admin-only through `POST /api/users`.
- Gotcha: login's `username` field is matched against the stored **email**.

Frontend BFF (the template's main value):
- Session cookie is httpOnly and encrypted with `jose` (`dir`/`A256GCM`), name `__Host-app_session` in prod, `app_session_dev` in dev. It stores access token, refresh token, expiries and user. `SESSION_SECRET` must be at least 32 chars. See `lib/auth/session.ts`.
- BFF handlers call the API with `Bearer`; on 401 they refresh via `POST /api/auth/refresh` and rotate the cookie. See `lib/auth/api-session.ts` and `lib/bff/proxy.ts`.
- Double-submit CSRF: non-httpOnly cookie + `x-csrf-token` header, enforced in `middleware.ts` for mutating `/api/bff/*` and `/api/auth/logout` when `CSRF_ENFORCE` is on (defaults to production). Origin allowlist from `CSRF_TRUSTED_ORIGINS`. See `lib/security/csrf.ts`.
- Typed errors `{ code, message, traceId }` with codes `UNAUTHORIZED | SESSION_EXPIRED | FORBIDDEN | BAD_REQUEST | UPSTREAM_ERROR | CSRF_REJECTED`. Trace id comes from `x-request-id`/`x-correlation-id`, else a UUID. See `lib/bff/http.ts`. Request logs use `[bff.request]` via `lib/bff/observability.ts`.
- Route handlers live under `app/api/auth/{login,refresh,logout,session}` and `app/api/bff/users/*`.
- Client helpers: `csrfFetch` adds the CSRF header; `parseApiError` redirects to `/login?reason=session_expired` on 401. See `lib/security/csrf-client.ts`, `lib/bff/client-session.ts`.

## How to add a vertical slice
Concrete example: the Users feature. Follow the same shape.

1. Entity: `Deals.Models/Entities/<Entity>.cs`, `[Table("<snake_case>")]`, inherit `BaseModel` (Created/Updated/CreatedBy/UpdatedBy are audited in `AppDbContext.SaveChanges`). Map every column with `[Column]`.
2. DbContext: add `DbSet` and `OnModelCreating` mapping in `Deals.BusinessLogic/Context/AppDbContext.cs`. Add DDL to `SQL/schema.sql` (source of truth); for existing DBs add a dated `SQL/migrations/*.sql`.
3. Repository: no per-entity repository. Use the generic `IRepository` methods (`Get<T>`, `GetTrack<T>`, `GetByIdAsync`, `Save`, `SaveUpdate`, `RemoveAsync`, `ExecuteInTransactionAsync`, ...) in `Deals.BusinessLogic/Services/Repository.cs`. Only extend `IRepository` for a genuinely new generic operation.
4. Service: interface in `Deals.BusinessLogic/Interfaces/I<Feature>Service.cs`, implementation in `Deals.BusinessLogic/Services/<Feature>Service.cs`. Inject `IRepository` (and `IPasswordService` for auth). Put validation here and throw `ArgumentException`.
5. DI: register scoped in `Deals.API/Extensions/ServiceCollectionExtensions.cs`.
6. Controller: `Deals.API/Controllers/<Feature>Controller.cs` with `[ApiController]`, `[Route("api/[controller]")]`, `[Authorize(Policy = "UserWithId")]` (use `AdminWithId` for admin-only, as `UsersController` does). Map with `.Adapt<T>()`.
7. DTOs: `Deals.API/Models/<Feature>/*.cs` (request and response types with validation attributes). Business-only DTOs go in `Deals.BusinessLogic/Models/<Feature>/`.
8. Frontend:
   - Contract plus normalize/validate helpers: `Deals.Web/lib/contracts/<feature>.ts`.
   - BFF route: `Deals.Web/app/api/bff/<feature>/route.ts` (add `[id]/route.ts` as needed). Use `getServerSession()`, `fetchApiWithAutoRefresh`, `attachSessionCookie`, and `forbidden()` for admin-only.
   - Client API: `Deals.Web/app/<feature>/_lib/<feature>-api.ts` using `csrfFetch` + `parseApiError`.
   - Page: `Deals.Web/app/<feature>/page.tsx` (server component: `getServerSession` or `requireAdminSession`) plus client components beside it.
9. Wire navigation: add the route to `Deals.Web/components/navigation/nav-config.ts`. Route protection is opt-out: the global `config.matcher` in `Deals.Web/middleware.ts` already covers every app route, and `middleware.ts` calls `isPublicRoute` (`lib/security/route-policy.ts`) — a route is public only if it is listed there. There is no `privateRoutes` list to update.
10. Verify: `dotnet build Deals.sln` and `pnpm build` in `Deals.Web`.

## Rename the template for a new project
Technical project names are `Deals.*`; visible product branding is `Deals Steam MX`. Do not add price-provider routes or integrations until their contracts are defined.

## Config
Backend (env vars or `appsettings.json`):
```
Jwt__Key                       # >= 32 UTF-8 bytes; SET_* or short values throw at startup
Jwt__Issuer                    # default Deals
Jwt__Audience                  # default DealExtUsers
Jwt__ExpirationHours           # optional, default 2
Auth__MaxFailedAttempts        # default 5
Auth__LockMinutes              # default 15
Auth__RefreshDays              # default 30
ConnectionStrings__DefaultConnection
Cors__AllowedOrigins           # array or comma string; fallback http://localhost:3000
```
Frontend:
```
API_BASE_URL                   # default http://localhost:5181
SESSION_SECRET                 # >= 32 chars
CSRF_ENFORCE                   # true|false; default is production-only
CSRF_TRUSTED_ORIGINS           # comma-separated origins
```
Copies: `.env.example` (root) and `Deals.Web/.env.example`. Never commit real secrets. The checkout's `appsettings.json` only holds `SET_*` placeholders; `.env` and `Deals.API/appsettings.json` are listed in `.gitignore`.

## Docker
- `docker compose up -d --build`.
- `frontend` builds `Deals.Web` (`node:24-alpine`, `pnpm install --frozen-lockfile`), container `dealext-frontend`, host port 3010 -> container 3000.
- `api` builds `Dockerfile.api` (.NET 9), container `dealext-api`, host port 5010 -> container 8080.
- `api` joins `dealext-network` plus the external network `server-data`. Postgres is expected to already exist outside this compose file, so create the network first if needed:
```bash
docker network create server-data
```
- `docker compose up` is therefore not standalone. It needs a reachable Postgres matching `ConnectionStrings__DefaultConnection`.

## Gotchas
- No EF migrations tooling. Apply `SQL/schema.sql` (or the dated migration) by hand.
- No test projects. Minimum verification is a build plus a live smoke of bootstrap-admin/login.
- Keep the solution on .NET 9.
- New registrations are created with `Active=true, Admin=false`. Set `admin=true` in the DB (or via a seeded admin) before `/users` is reachable.
- There is no `privateRoutes` list. A new page is protected by default: the global `config.matcher` in `Deals.Web/middleware.ts` covers every app route and `isPublicRoute` (`Deals.Web/lib/security/route-policy.ts`) is the only public allow-list. Add a page to that file only when it must be public.
- The `ExceptionHandler` titles are Spanish; that is existing behavior, not a bug.
- The CSRF cookie is JS-readable by design (double-submit). The session cookie stays httpOnly.
- CORS only matters for direct browser-to-API calls. The normal path is server-side BFF proxying.
