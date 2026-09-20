-- Epic Games Store refresh timestamp. Epic prices are ordinary rows in game_offers with source = 'epic'
-- (one row per game: the store sells one base offer per game, so offer_key is the constant 'store'), so
-- no per-store price or currency columns are needed; only the gate that decides when to call Epic again.
-- Independent of offers_refreshed_at / ggdeals_refreshed_at / bundles_refreshed_at: an Epic failure must
-- not mark the ITAD or gg.deals snapshot as fresh, nor the other way around.

ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS epic_refreshed_at TIMESTAMPTZ;

ANALYZE public.steam_games;
