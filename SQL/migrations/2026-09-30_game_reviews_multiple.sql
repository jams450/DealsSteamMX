-- Multiple reviews per (user_id, game_id, platform).
--
-- A game can be played more than once: a run finished in 2026 and another in 2030 are two reviews, and
-- both must survive. The old uq_game_reviews constraint forced one row per (user, game, platform), so a
-- second review either overwrote the first or was rejected. Dropping it makes room for the history; the
-- index below keeps the (user, game, platform) lookup the library list and the game detail use.

ALTER TABLE public.game_reviews DROP CONSTRAINT IF EXISTS uq_game_reviews;

CREATE INDEX IF NOT EXISTS idx_game_reviews_user_game_platform
    ON public.game_reviews(user_id, game_id, platform);

ANALYZE public.game_reviews;
