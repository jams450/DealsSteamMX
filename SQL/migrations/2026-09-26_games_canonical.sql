-- Canonical game identity, decoupled from steam_games (docs/PLAN_CATALOG.md).
-- games + game_external_ids are the shared identity row; steam_games.game_id and user_library.game_id
-- are nullable, derived links. No existing anchor (game_offers, steam_price_observations,
-- external_bundle_games) changes and no data moves. DDL only: the data backfill is
-- 2026-09-27_games_backfill.sql.

CREATE TABLE IF NOT EXISTS public.games (
    game_id BIGSERIAL PRIMARY KEY,
    title VARCHAR(512) NOT NULL,
    normalized_title VARCHAR(512) NOT NULL,   -- lowercase, no punctuation, no trailing edition token
    type VARCHAR(32) NULL,
    image_url VARCHAR(512) NULL,
    is_free BOOLEAN NOT NULL DEFAULT FALSE,
    release_year INT NULL,
    first_release_date DATE NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100) NULL,
    updated_by VARCHAR(100) NULL
);

-- Not UNIQUE on purpose: normalized_title compares and displays, it never asserts identity.
CREATE INDEX IF NOT EXISTS idx_games_normalized_title ON public.games(normalized_title);

-- The durable identity. namespace reuses the user_library.store vocabulary plus provider namespaces
-- (steam, itad, igdb, hltb, ...). (namespace, external_id) is the real key, game_id is what propagates.
CREATE TABLE IF NOT EXISTS public.game_external_ids (
    game_external_id BIGSERIAL PRIMARY KEY,
    game_id BIGINT NOT NULL REFERENCES public.games(game_id) ON DELETE CASCADE,
    namespace VARCHAR(32) NOT NULL,
    external_id VARCHAR(64) NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100) NULL,
    updated_by VARCHAR(100) NULL,
    CONSTRAINT uq_game_external_ids UNIQUE (namespace, external_id)
);

CREATE INDEX IF NOT EXISTS idx_game_external_ids_game ON public.game_external_ids(game_id);

-- Nullable derived links: NOT NULL is deliberately not set. user_library.game_id in particular keeps
-- NULL as a valid "no canonical identity" state.
ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS game_id BIGINT NULL REFERENCES public.games(game_id);

ALTER TABLE public.user_library
    ADD COLUMN IF NOT EXISTS game_id BIGINT NULL REFERENCES public.games(game_id);

CREATE INDEX IF NOT EXISTS idx_steam_games_game ON public.steam_games(game_id);
CREATE INDEX IF NOT EXISTS idx_user_library_game ON public.user_library(game_id);

ANALYZE public.games;
ANALYZE public.game_external_ids;
