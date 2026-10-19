-- Persist annual per-user tier-list placement for games with a dated review.
-- Review membership is derived from game_reviews; unranked entries have no row here.
CREATE TABLE IF NOT EXISTS public.user_game_tiers (
    user_id INT NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    year INT NOT NULL,
    game_id BIGINT NOT NULL REFERENCES public.games(game_id) ON DELETE CASCADE,
    tier VARCHAR(1) NOT NULL,
    position INT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    PRIMARY KEY (user_id, year, game_id)
);

CREATE INDEX IF NOT EXISTS idx_user_game_tiers_order
    ON public.user_game_tiers(user_id, year, tier, position);
