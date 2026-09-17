-- Provider-neutral offer metadata: the DRM and platform names reported with a shop offer.
-- game_offers stays a current snapshot (upsert on the unique key), so both arrays are overwritten on
-- every successful offer refresh; empty '{}' means the provider reported none.
-- Names only: provider ids are never stored, so no id mapping or lookup table is needed.

ALTER TABLE public.game_offers
    ADD COLUMN IF NOT EXISTS drm_names TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS platform_names TEXT[] NOT NULL DEFAULT '{}';

ANALYZE public.game_offers;
