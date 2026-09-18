# Deals Steam MX

Deals Steam MX starts as a reusable full-stack foundation: .NET 9 backend API + Next.js 15 frontend BFF + PostgreSQL. It includes JWT auth, refresh sessions, CSRF protection, and an admin Users reference slice. Deal discovery and price-provider integrations are not implemented yet.

## Prerequisites

- .NET SDK 9
- Node 20+ and `pnpm`
- PostgreSQL 15+ (or Docker for the containerized path)
- Docker and Docker Compose (optional)

## Project layout

```text
DealExt/
  Deals.sln                        # solution
  .env.example                     # backend environment template
  Deals.API/                       # ASP.NET Core web API (net9.0)
    Program.cs                     # composition root
    Extensions/                    # auth, db, cors, rate limiting setup
    Controllers/                   # Auth, Me, Users
    Models/                        # DTOs
  Deals.BusinessLogic/             # services and generic repository
    Context/AppDbContext.cs        # EF DbContext + audit
  Deals.Models/                    # entities and base models
  Deals.Web/                       # Next.js 15 App Router BFF
    app/api/auth/                  # BFF login/logout/refresh/session routes
    app/api/bff/users/             # BFF proxy to backend /api/users
    lib/auth/session.ts            # encrypted httpOnly session cookie
    lib/bff/proxy.ts               # Bearer proxy with auto-refresh
    lib/security/csrf.ts           # double-submit CSRF
    middleware.ts                  # session + CSRF gating
  SQL/
    schema.sql                     # users + user_sessions source of truth
    migrations/                    # dated manual migrations
```

## Configure environment

```bash
cp .env.example .env
cd Deals.Web
cp .env.example .env
```

Backend keys: `Jwt`, `Auth`, `BootstrapAdmin`, `ConnectionStrings`, `Cors`. Frontend keys: `API_BASE_URL`, `SESSION_SECRET`, `CSRF_ENFORCE`, `CSRF_TRUSTED_ORIGINS`. Set `BootstrapAdmin__Name`, `BootstrapAdmin__Email`, and `BootstrapAdmin__Password` on first startup when database has no administrator. Never commit real secrets.

## Set up database

Apply the schema to Postgres:

```bash
psql -U postgres -d dealext -f SQL/schema.sql
```

For an existing database, apply the dated file under `SQL/migrations/`. The schema contains `users` and `user_sessions` only.

## Run locally

Backend:

```bash
dotnet build Deals.sln
dotnet run --project Deals.API/Deals.API.csproj
```

Frontend:

```bash
cd Deals.Web
pnpm install --no-frozen-lockfile
pnpm dev
```

Run both from the repository root:

```bash
pnpm install
pnpm dev
```

## Docker

The API container needs an external Postgres and pre-existing network:

```bash
docker network create shared-db-network
docker compose up -d --build
```

Frontend: `http://localhost:3000`. API: `http://localhost:5000`. Containers: `dealext-frontend`, `dealext-api`.

## First login

Set bootstrap credentials before starting the API when database has no administrator:

```bash
export BootstrapAdmin__Name="Initial Admin"
export BootstrapAdmin__Email="admin@example.com"
export BootstrapAdmin__Password="replace-with-a-strong-password"
```

Seeder creates one active administrator and skips itself once any administrator exists. New users can then be created only by an authenticated administrator through `POST /api/users`. Login matches `username` to the stored email.

## Reference

`AGENTS.md` documents the architecture, auth/BFF model, vertical-slice conventions, and verification commands. `Deals.Web/DESIGN.md` and `Deals.Web/THEME_COLORS.md` define the UI contract.
