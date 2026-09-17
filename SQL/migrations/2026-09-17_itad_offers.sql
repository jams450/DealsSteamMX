-- ITAD offer snapshot: maps a Steam game to its IsThereAnyDeal id and stores one row per shop.
-- game_offers is a provider-neutral current snapshot (upsert on the unique key), NOT history:
-- append-only price history remains in steam_price_observations.
-- The MXN columns are derived values; the original price is never overwritten.

ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS itad_game_id VARCHAR(36),
    ADD COLUMN IF NOT EXISTS offers_refreshed_at TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS public.game_offers (
    game_offer_id BIGSERIAL PRIMARY KEY,
    steam_game_id INT NOT NULL REFERENCES public.steam_games(steam_game_id) ON DELETE CASCADE,
    source VARCHAR(16) NOT NULL,
    offer_key VARCHAR(128) NOT NULL,
    shop_id VARCHAR(32),
    shop_name VARCHAR(128) NOT NULL,
    classification VARCHAR(16) NOT NULL,
    original_currency VARCHAR(3) NOT NULL,
    original_regular_price_minor INT,
    original_current_price_minor INT,
    mxn_regular_price_minor INT,
    mxn_current_price_minor INT,
    fx_rate NUMERIC(18,8),
    fx_rate_date DATE,
    fx_source VARCHAR(32),
    pricing_type VARCHAR(16) NOT NULL,
    discount_percent INT,
    deal_url VARCHAR(1024),
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_game_offers UNIQUE (steam_game_id, source, offer_key)
);

CREATE INDEX IF NOT EXISTS idx_game_offers_game ON public.game_offers(steam_game_id);

ANALYZE public.game_offers;
