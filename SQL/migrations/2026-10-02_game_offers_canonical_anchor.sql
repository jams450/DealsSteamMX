-- Canonical anchor for game_offers (docs/PLAN_MULTISTORE.md §5, Fase 0).
--
-- game_offers hung off steam_game_id, so a game with no Steam row could not hold an offer at all. This
-- adds the provider-neutral anchor (game_id, region), taken from steam_games on backfill, and the unique
-- key that will replace uq_game_offers once no row is left without a canonical game.
--
-- region is NOT NULL: the price of an offer depends on the country, and a unique index cannot dedupe a
-- NULL (Postgres treats NULLs as distinct), which would silently allow two rows for the same
-- (game, source, shop). Every row has a steam_game_id, so every row backfills and SET NOT NULL cannot
-- fail; if it ever did, failing loudly is the point.
--
-- steam_game_id stays NOT NULL on purpose. Nothing writes a Steam-less offer yet, so relaxing it now
-- would only weaken the constraint that still dedupes today. The phase that actually writes one
-- (PLAN_MULTISTORE Fase 2, no la 1: la 1 sigue colgando del steam_game_id del juego) is the one that
-- relaxes it, together with the offer upsert that stops keying off the SteamGame navigation.

ALTER TABLE public.game_offers
    ADD COLUMN IF NOT EXISTS game_id BIGINT NULL REFERENCES public.games(game_id);

ALTER TABLE public.game_offers
    ADD COLUMN IF NOT EXISTS region VARCHAR(2) NULL;

UPDATE public.game_offers AS o
   SET game_id = g.game_id,
       region = g.region,
       updated_at = NOW()
  FROM public.steam_games AS g
 WHERE o.steam_game_id = g.steam_game_id
   AND (o.game_id IS DISTINCT FROM g.game_id OR o.region IS DISTINCT FROM g.region);

ALTER TABLE public.game_offers
    ALTER COLUMN region SET NOT NULL;

CREATE INDEX IF NOT EXISTS idx_game_offers_game_region ON public.game_offers(game_id, region);

-- The future key: one offer per (canonical game, country, source, shop). uq_game_offers is kept because
-- it is still the only thing deduping while game_id can be NULL. Drop it when this returns 0:
--   SELECT count(*) FROM public.game_offers WHERE game_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_game_offers_canonical
    ON public.game_offers(game_id, region, source, offer_key);

ANALYZE public.game_offers;
