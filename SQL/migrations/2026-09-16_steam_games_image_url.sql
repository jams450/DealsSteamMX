-- Cached Steam artwork per (app_id, region). Detail header_image is preferred; the search tiny_image
-- only fills the column when it is still empty, so a richer image is never downgraded.

ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS image_url VARCHAR(512);

ANALYZE public.steam_games;
