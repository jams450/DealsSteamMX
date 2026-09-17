-- gg.deals refresh timestamp plus the provider-neutral history-low columns.
-- gg.deals prices are ordinary rows in game_offers with source = 'ggdeals' (two rows per game:
-- retail and keyshop), so no per-provider price, currency or timestamp columns are needed.
-- history_low_all_minor / history_low_currency are generic, not gg.deals-specific: ITAD fills them
-- from historyLow.all and gg.deals from historicalRetail / historicalKeyshops.

ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS ggdeals_refreshed_at TIMESTAMPTZ;

ALTER TABLE public.game_offers
    ADD COLUMN IF NOT EXISTS history_low_all_minor INT,
    ADD COLUMN IF NOT EXISTS history_low_currency VARCHAR(3);

ANALYZE public.steam_games;
ANALYZE public.game_offers;
