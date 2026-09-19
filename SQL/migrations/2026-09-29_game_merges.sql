-- Manual canonical merge (log). Identity is repointed, never aliased: the absorbed games row is
-- deleted after every referrer has moved. No FK on purpose: absorbed_game_id no longer exists and
-- the log must survive its own subject. Undo is deferred to v1.1; this log is the only record of
-- what moved, so it ships in v1.
--
-- The merge itself repoints, in this exact order: game_external_ids, steam_games, user_library,
-- game_reviews (reviews colliding on (user_id, platform) are deleted first). Reversing that order
-- either raises 23503 (steam_games/user_library are NO ACTION) or silently cascade-deletes reviews.

CREATE TABLE IF NOT EXISTS public.game_merges (
    game_merge_id      BIGSERIAL PRIMARY KEY,
    survivor_game_id   BIGINT NOT NULL,
    absorbed_game_id   BIGINT NOT NULL,
    absorbed_snapshot  JSONB  NOT NULL,
    moved_external_ids INT NOT NULL,
    moved_steam_games  INT NOT NULL,
    moved_library_rows INT NOT NULL,
    dropped_reviews    INT NOT NULL,
    merged_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    merged_by          VARCHAR(100)
);

CREATE INDEX IF NOT EXISTS idx_game_merges_survivor ON public.game_merges(survivor_game_id);
