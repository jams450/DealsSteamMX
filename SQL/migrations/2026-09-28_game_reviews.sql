-- Per-platform reviews (docs/PLAN_LIBRARY.md §9, docs/PLAN_IMPLEMENTACION_BIBLIOTECA.md §7).
-- Keyed by (user_id, game_id, platform) and deliberately NOT foreign-keyed to user_library: a
-- user_library row is an import artifact whose unique key includes state, so a reimport or a state
-- change recreates it. (game_id, platform) is stable and survives both. The score label is computed
-- (ReviewScoreBands), never persisted; no CHECK constraints, matching the rest of the schema.

CREATE TABLE IF NOT EXISTS public.game_reviews (
    game_review_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    game_id BIGINT NOT NULL REFERENCES public.games(game_id) ON DELETE CASCADE,
    platform VARCHAR(32) NOT NULL,   -- same vocabulary as user_library.store
    started_month DATE,              -- day 1 of the month; the frontend uses <input type="month">
    finished_month DATE,
    score SMALLINT,                  -- 0..100, validated in the service
    is_goty BOOLEAN NOT NULL DEFAULT FALSE,
    body TEXT,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100) NULL,
    updated_by VARCHAR(100) NULL,
    CONSTRAINT uq_game_reviews UNIQUE (user_id, game_id, platform)
);

CREATE INDEX IF NOT EXISTS idx_game_reviews_game ON public.game_reviews(game_id);

ANALYZE public.game_reviews;
