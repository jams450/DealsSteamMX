-- Local historical low per (app_id, region): lowest current price observed locally for the row's currency.
-- Steam does not provide it. Minor units, observed UTC timestamp.

ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS lowest_price_minor INT,
    ADD COLUMN IF NOT EXISTS lowest_price_at TIMESTAMPTZ;

ANALYZE public.steam_games;
