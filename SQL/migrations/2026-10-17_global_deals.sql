-- Global ITAD deals feed (spec S1/S4, task T7).
--
-- One individual deal observed from the global ITAD deals catalog (deals/v2), keyed by
-- (source, deal_key). It is its own table, deliberately not external_bundles: a deal is not a bundle and
-- this feed is never an offer in game_offers, so it cannot disturb the per-game price comparison.
--
-- source is the dedicated literal 'itad_global_deals', isolated from the per-game 'itad' source the
-- orphan purge scopes to and from 'itad_global' (the bundle sweep). A complete sweep upserts every row
-- and removes only rows of its own source; a failed/truncated/cursor-loop sweep writes nothing and the
-- previous snapshot stands.
--
-- The original currency/amount columns are the source of truth. mxn_* and pricing_type are derived:
--   regional      - provider quoted MXN; mxn_current_price_minor is the same amount
--   fx_estimate   - provider quoted USD; mxn_current_price_minor was converted with fx_rate/fx_rate_date
--   unconverted   - any other currency: mxn_current_price_minor is NULL, no MXN claim, not price-eligible
-- history_low_* is filled only when the provider actually reported a low; it is never synthesized.

CREATE TABLE IF NOT EXISTS public.global_deals (
    global_deal_id                  BIGSERIAL PRIMARY KEY,
    source                          VARCHAR(32)  NOT NULL,
    deal_key                        VARCHAR(128) NOT NULL,
    itad_game_id                    VARCHAR(36)  NOT NULL,
    game_title                      VARCHAR(512) NOT NULL,
    game_slug                       VARCHAR(256) NULL,
    game_type                       VARCHAR(32)  NULL,
    game_mature                     BOOLEAN      NOT NULL DEFAULT FALSE,
    shop_id                         VARCHAR(32)  NOT NULL,
    shop_name                       VARCHAR(128) NOT NULL,
    is_official                     BOOLEAN      NOT NULL DEFAULT FALSE,
    original_currency               VARCHAR(3)   NOT NULL,
    original_current_price_minor    INT          NOT NULL,
    original_regular_price_minor    INT          NULL,
    discount_percent                INT          NULL,
    history_low_all_minor           INT          NULL,
    history_low_currency            VARCHAR(3)   NULL,
    mxn_current_price_minor         INT          NULL,
    fx_rate                         NUMERIC(18,8) NULL,
    fx_rate_date                    DATE         NULL,
    fx_source                       VARCHAR(32)  NULL,
    pricing_type                    VARCHAR(16)  NOT NULL,
    deal_url                        VARCHAR(1024) NULL,
    expires_at                      TIMESTAMPTZ  NULL,
    observed_at                     TIMESTAMPTZ  NOT NULL,
    created_at                      TIMESTAMPTZ  DEFAULT CURRENT_TIMESTAMP,
    updated_at                      TIMESTAMPTZ  DEFAULT CURRENT_TIMESTAMP,
    created_by                      VARCHAR(100),
    updated_by                      VARCHAR(100),
    CONSTRAINT uq_global_deals UNIQUE (source, deal_key)
);

-- Wishlist exclusion matches on the exact ITAD game id.
CREATE INDEX IF NOT EXISTS idx_global_deals_itad_game
    ON public.global_deals(itad_game_id);

-- Feed scan: good, price-eligible deals ordered by discount, newest first.
CREATE INDEX IF NOT EXISTS idx_global_deals_feed
    ON public.global_deals(pricing_type, discount_percent DESC, observed_at DESC);

ANALYZE public.global_deals;
