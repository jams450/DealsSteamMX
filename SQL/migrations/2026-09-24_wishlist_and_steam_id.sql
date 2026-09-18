-- Steam account link + per-user store library/wishlist snapshot.
-- users.steam_id64 is admin-managed (17-digit SteamID64); users.wishlist_synced_at marks the last
-- wishlist import; users.wishlist_state records that attempt's outcome ("ok" | "inaccessible") so a
-- private profile stays distinguishable from a never-synced one. user_library keeps one row per
-- (user, store, store_game_id, state).

ALTER TABLE public.users
    ADD COLUMN IF NOT EXISTS steam_id64 VARCHAR(20) NULL,
    ADD COLUMN IF NOT EXISTS wishlist_synced_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS wishlist_state VARCHAR(16) NULL;

CREATE TABLE IF NOT EXISTS public.user_library (
    user_library_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    itad_game_id VARCHAR(36) NULL,
    store VARCHAR(32) NOT NULL,
    store_game_id VARCHAR(64) NOT NULL,
    title VARCHAR(256) NOT NULL,
    state VARCHAR(16) NOT NULL,
    is_installed BOOLEAN NULL,
    priority INT NULL,
    added_at TIMESTAMPTZ NULL,
    imported_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW(),
    created_by VARCHAR(100) NULL,
    updated_by VARCHAR(100) NULL,
    CONSTRAINT uq_user_library UNIQUE (user_id, store, store_game_id, state)
);

CREATE INDEX IF NOT EXISTS idx_user_library_user_itad ON public.user_library(user_id, itad_game_id);
CREATE INDEX IF NOT EXISTS idx_user_library_user_title ON public.user_library(user_id, lower(title));

ANALYZE public.users;
ANALYZE public.user_library;
