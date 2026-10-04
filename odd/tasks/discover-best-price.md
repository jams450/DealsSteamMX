# Discover: cheapest price including Steam

## Objective
Preserve /discover UI; each card reports minimum comparable persisted MXN price across Steam and all stores, with matching source metadata.

## Scope and constraints
- SteamGameService.GetDiscoverAsync calculation/projection only; bounded regression checks if practical.
- No live provider calls, schema changes, secrets, or unrelated edits.
- Include fx_estimate; exclude unconverted and non-MX/non-MXN offers. Steam wins ties.
- Preserve DTO, layout, filters and ordering rules. Existing unrelated dirty files remain untouched.
- No commits: user has not requested them.

## Tasks
- [x] T1 (done): Fix minimum and all five winning metadata fields. Parent readback confirmed correction.
- [x] T2 (done): Independent corrected backend build and diff checks passed. Native preflight performed; review not started because candidate includes unrelated local changes. Runtime/SQL validation remains pending.

## Acceptance
Steam cheaper, store cheaper (including FX), equal, no offers, excluded offers: correct price, source and fallback. Query stays EF-translatable and read-only.

## Evidence
Read-only exploration found Steam excluded whenever bestOffer exists (SteamGameService.cs:399-401). No existing test project. Current branch main; unrelated local changes present. Corrected dotnet build Deals.sln --no-restore passed (0 warnings/errors); diff check passed. Seven-case Python logic model passed, not C#/EF runtime evidence. No DB/browser smoke, generated SQL verification, commit or deploy.

## Next step
Smoke /discover after deployment with known Steam-cheaper and store-cheaper games; verify actual EF query and source labels. Review native isolated candidate if requested.
