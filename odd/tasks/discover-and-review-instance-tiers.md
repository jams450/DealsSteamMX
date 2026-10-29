# Discover and review-instance tiers
Objective: match wishlist grid, retire deals UI, rank individual reviewed plays. Baseline: main at 4da2b921dc1081bc8362636c260fc135737048f0.
Delivery: user subsequently requested commit on main; no push or PR requested. Checks: node --test, dotnet build, pnpm build; DB migration is manual, never apply to configured DB.

## Specs
S1. User: "En /discover que los juegos sean 5 como en wishlist me refiero a la cuadricula". Match wishlist responsive grid (md2/xl4/2xl5), not page size.
S2. User: "/deals no ha funcionado nada si no tenemos nada mejor quitarlo". Retire nav and deals UI; legacy URL redirects to discover. Keep unused backend feed/data; no destructive cleanup.
S3. User: "en /library/tiers que el ano sea un combo con base a las resenas del ano jugado". Options from authenticated user's dated reviews, using finished month then started month. Handle empty years, errors, stale requests.
S4. User: "si en alguna resena tiene goty que tenga un marco diferente". Each review card shows platform and distinct GOTY frame plus text.
S5. User: "si se ha jugado 2 veces al ano en diff plataformas mostrar 2 y mostrar la plataforma". Clarification: "Sí, rangos independientes". Each review is a distinct independently saved/dragged card, including repeat same-platform plays. Ownership and year checked server-side.
S6. Preserve old placements: copy old game/year placement to every matching review, then allow independent moves. Remove stale orphan placements with no dated matching review only in migration, documenting behavior. Preserve merge integrity and review-delete cascade. Prepare migration only, do not apply to live database. Existing unrelated .env.example change untouched.

## Tasks
T1 [S3-S6] done; writer implemented requested behavior and preservation migration; contract/helpers/backend checks GREEN after observed missing API/module RED. Parent confirmed 23 focused tests and production frontend build exit 0; independent checks tracked in T3.
T2 [S1-S2] done; inline grid matches wishlist, nav hides deals, legacy route redirects. Structural tests 13/13 pass; no functional RED claimed (source assertions added after edits). Initial new-test regex syntax error corrected and rerun passed.
T3 [S1-S6] done; independent verification (mvtosbsr-3-4mfb) completed with no blockers. Current/baseline dotnet builds exit 0; standalone tsc error sets byte-identical (10 pre-existing fixture errors). Parent pnpm build exit 0 in 19s, 24/24 focused tests PASS, final GameTiers.Checks exit 0 after adding verifier boundary probes. SQL runtime and visual browser proof unavailable; migration static review passed. No migration executed; no commit/push requested. Generated tsconfig.tsbuildinfo restored to original baseline. Native review unavailable: inspect stopped with managed_assets_outdated; no native approval claimed.

## Log
L1. Original: "Requiero varios cambios En /discover que los juegos sean 5 como en wishlist me refiero a la cuadricula, /deals no ha funcionado nada si no tenemos nada mejor quitarlo, en /library/tiers que el ano sea un combo con base a las resenas del ano jugado y si en alguna resena tiene goty que tenga un marco diferente. tambien considerar que si se ha jugado 2 veces al ano en diff plataformas mostrar 2 y mostrar la plataforma"
L2. User clarified independent ranks per play. Scout found game/year primary key, client game dedupe, merge collision delete, and no review-years endpoint. Migration must avoid lossy latest-review-only backfill: replicate prior placement instead.
