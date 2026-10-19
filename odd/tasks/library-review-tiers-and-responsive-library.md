# Library review, cover, and responsive-navigation work
Authorized from the user's request to begin all previously identified points.
Feature scope: validate `/library` filters and cover-sync behavior, improve the top bar on short screens, and add an annual review tier list.

## Specs
1. S1 — User: "Validate all filters in `/library`" (from the task context). Inspect each existing filter, its counts, composition, sorting/search interactions; fix only demonstrated defects.
2. S2 — User: "add a small job to fill missing game covers, trying Steam first and IGDB second." Preserve bounded execution, only fill missing covers, and order Steam before IGDB.
3. S3 — User: "agrega arreglat top bar muchos iconos no funciona bien en pantallas cortas, si se puede agrupa". Improve top-bar usability on short/narrow screens; group navigation/actions where feasible, preserve accessible interactions.
4. S4 — User requested a view/filter gathering all reviewed games from a year and organizing them as a tier list; clarified: "Arrastrar entre rangos S–F" and "Persistentes por usuario". Provide annual reviewed-game view, draggable S–F tiers, and persistence per user.
5. S5 — User: "empieza a trabajar en cada punto". Work each enumerated point; preserve existing unrelated/untracked files.
6. S6 — Clarified tier scale: six ranges `S, A, B, C, D, F` (no `E` row).
7. S7 — A tier placement must survive a canonical game merge: `user_game_tiers.game_id` is `ON DELETE CASCADE`, so the merge has to repoint (or deduplicate) it exactly like `user_game_favorites`, and never lose a placement silently.

## Tasks
- T1 — S1 — inline: audit and test library filter semantics; commit: pending.
- T2 — S2 — inline: confirm bounded cover pass source ordering and adapt if needed; commit: pending.
- T3 — S3 — inline: responsive navigation/topbar grouping; commit: pending.
- T4 — S4/S6 — inline: annual reviews and persistent S–F tiers across frontend/API/database; commit: pending. Backend + contract + BFF + page/UI written; six-tier scale applied on both sides.
- T5 — S7 — inline: `GameMergeService` repoints `user_game_tiers` with pre-collision delete, audits it in the JSONB snapshot and in `merge_counters`, and `SQL/checks/merge-invariants.sql` gains check 5; commit: pending.

## Log
- L1: "empieza a trabajar en cada punto"
- L2: User selected "Arrastrar entre rangos S–F" for the tier-list interaction.
- L3: User selected "Persistentes por usuario" for tier-list storage.
- L4: User selected the six-range scale: `S, A, B, C, D, F` (no `E`).
- L5: Independent verification (gentle-ai-verify) found blocker B1: the canonical merge deleted `user_game_tiers` rows by cascade without auditing or repointing them. Fixed in T5; the survivor's placement wins a `(user_id, year)` collision and the dropped absorbed placement is counted, never silent.
