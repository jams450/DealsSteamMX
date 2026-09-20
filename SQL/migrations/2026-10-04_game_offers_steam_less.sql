-- Ofertas de juegos que no tienen fila de Steam (docs/PLAN_MULTISTORE.md §6, Fase 2).
--
-- Hasta aquí toda oferta colgaba de un steam_games. Un juego de Xbox que no está en Steam no tiene esa
-- fila, así que su oferta necesita steam_game_id nulo y el ancla canónica (game_id, region) que la Fase 0
-- ya añadió.
--
-- uq_game_offers NO se borra aquí, en contra de lo que decía el plan. Sigue siendo el único dedupe de las
-- ofertas ancladas a Steam, y una fila con steam_game_id nulo no lo debilita (Postgres trata los NULL como
-- distintos). El borrado toca el día que ningún escritor pueda dejar game_id nulo, porque es game_id nulo
-- lo que deja a uq_game_offers_canonical sin cubrir la fila. Mientras tanto, el contrato es que una oferta
-- sin Steam tiene game_id obligatorio: se escribe por (game_id, region, source, offer_key), nunca a ciegas.
ALTER TABLE public.game_offers
    ALTER COLUMN steam_game_id DROP NOT NULL;

ANALYZE public.game_offers;
