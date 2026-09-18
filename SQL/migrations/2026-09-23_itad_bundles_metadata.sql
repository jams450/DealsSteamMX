-- ITAD bundle metadata from the real `games/overview/v2` contract: bundles are flat entries with a
-- provider page, free-text details, a publish date and a sanitized tier list. tiers_json stores only the
-- display shape (priceMinor, currency, addon, item titles/types) as jsonb - never the raw payload and
-- never provider item ids.

ALTER TABLE public.external_bundles
    ADD COLUMN IF NOT EXISTS page_url VARCHAR(1024),
    ADD COLUMN IF NOT EXISTS details VARCHAR(600),
    ADD COLUMN IF NOT EXISTS published_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS tiers_json JSONB;

ANALYZE public.external_bundles;
