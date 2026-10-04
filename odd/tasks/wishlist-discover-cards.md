# Wishlist: readable Discover-style cards

## Objective
Larger Discover-style cards with always-visible best price, source badge, discount, provider historical minimum and Deal Score 0–10. Remaining information expands without losing actions.

## Scope
Wishlist API derived price provenance DTO, frontend contract/tests, WishlistCard and grid. No schema, provider calls, auth changes, global redesign or unrelated edits. Preserve filters, sorting, selection, package preview, categories, refresh, ownership and errors. User confirms Deal Score. No commits requested.

## Tasks
- [x] T1 (done): Additive winner metadata API/contract; worker observed RED (3 failures), GREEN (8/8 contract tests). Independent 33/33 wishlist tests and dotnet build passed (0 warnings/errors); Npgsql ToQueryString harness in /tmp translated reproduced grouped projection successfully (2 ROW_NUMBER); no DB execution evidence.
- [x] T2 (done): Larger card summary and full-width disclosure; pure helper RED then 18/18 GREEN. Category colors corrected and parent readback confirmed.
- [x] T3 (done): Independent backend/frontend builds and scoped diff passed; 38/38 wishlist tests before CSS-only correction, 18/18 metrics afterward. Native preflight includes unrelated edits, no review started. Authenticated visual/responsive smoke pending (session expired).

## Acceptance
Steam vs official source never guessed; keyshop wins only lower price, legitimate wins ties. Unknown metadata stays unknown. No price yields no source badge. Percent labelled against Steam base, not fabricated provider discount. History is generic provider minimum, never labelled Steam-specific. FX provenance retained if available. Existing behavior and handlers preserved. Mobile no horizontal overflow, keyboard-operable disclosure, no nested link/button interactions.

## Evidence
Exploration: WishlistController:599-616 official minimum includes Steam but drops source; history from provider offers:542-555. Existing node:test wishlist suites available. Current grid 4 columns and collapsed score/history contribute to poor scanning. Auth local redirects to login; no credentials to inspect. Existing unrelated dirty files preserved.

## Follow-up
- [ ] T4 (in_progress): Wide-screen horizontal card (fixed thumbnail column, content on the right) and 2-column grid up to 2xl, so several items are visible quickly. Mobile/tablet unchanged.

## Next step
Validate cards with admin session at /wishlist (mobile/desktop, keyboard disclosure), then deploy only when requested. No commit/deploy performed.
