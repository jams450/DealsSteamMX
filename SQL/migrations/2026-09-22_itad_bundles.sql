-- ITAD external bundles (V1, display only). Bundles are canonical rows keyed by (source, bundle_key)
-- plus one relation per game they were seen in. They are never stored in game_offers and never take
-- part in the price comparison; no provider payload is persisted, only sanitized display fields.

ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS bundles_refreshed_at TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS public.external_bundles (
    external_bundle_id BIGSERIAL PRIMARY KEY,
    source VARCHAR(16) NOT NULL,
    bundle_key VARCHAR(128) NOT NULL,
    title VARCHAR(512) NOT NULL,
    shop_id VARCHAR(32),
    shop_name VARCHAR(128),
    deal_url VARCHAR(1024),
    expires_at TIMESTAMPTZ,
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_external_bundles UNIQUE (source, bundle_key)
);

CREATE TABLE IF NOT EXISTS public.external_bundle_games (
    external_bundle_game_id BIGSERIAL PRIMARY KEY,
    external_bundle_id BIGINT NOT NULL REFERENCES public.external_bundles(external_bundle_id) ON DELETE CASCADE,
    steam_game_id INT NOT NULL REFERENCES public.steam_games(steam_game_id) ON DELETE CASCADE,
    source VARCHAR(16) NOT NULL,
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_external_bundle_games UNIQUE (external_bundle_id, steam_game_id)
);

CREATE INDEX IF NOT EXISTS idx_external_bundle_games_game
    ON public.external_bundle_games(steam_game_id, source);

ANALYZE public.steam_games;
ANALYZE public.external_bundles;
ANALYZE public.external_bundle_games;
