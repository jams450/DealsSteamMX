-- Steam authenticated game search, game snapshots and locally observed price history.
-- Historical low means lowest price observed locally; Steam does not provide it.

CREATE TABLE IF NOT EXISTS public.steam_games (
    steam_game_id SERIAL PRIMARY KEY,
    app_id INT NOT NULL,
    name VARCHAR(512) NOT NULL,
    type VARCHAR(32),
    is_free BOOLEAN NOT NULL DEFAULT FALSE,
    currency VARCHAR(3),
    initial_price_minor INT,
    current_price_minor INT,
    discount_percent INT,
    region VARCHAR(2) NOT NULL,
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_steam_games_app_region UNIQUE (app_id, region)
);

CREATE TABLE IF NOT EXISTS public.steam_price_observations (
    steam_price_observation_id BIGSERIAL PRIMARY KEY,
    steam_game_id INT NOT NULL REFERENCES public.steam_games(steam_game_id) ON DELETE CASCADE,
    currency VARCHAR(3),
    initial_price_minor INT,
    current_price_minor INT,
    discount_percent INT,
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100)
);

CREATE INDEX IF NOT EXISTS idx_steam_price_observations_game_observed
    ON public.steam_price_observations(steam_game_id, observed_at);

ANALYZE public.steam_games;
ANALYZE public.steam_price_observations;
