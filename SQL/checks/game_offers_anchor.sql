-- Invariantes del ancla canónica de game_offers (docs/PLAN_MULTISTORE.md §5, Fase 0).
-- Esperado: 0 filas en CADA consulta. Correr después de aplicar
-- SQL/migrations/2026-10-02_game_offers_canonical_anchor.sql y tras cualquier fusión de juegos.
-- Solo lectura: no modifica nada.

-- 1. Ninguna oferta con region NULL. El precio depende del país y la clave única no puede deduplicar
--    un NULL (Postgres los trata como distintos), así que un NULL aquí reabre los duplicados en silencio.
SELECT game_offer_id
FROM public.game_offers
WHERE region IS NULL;

-- 2. Ninguna oferta cuyo game_id/region contradiga el snapshot de Steam del que cuelga.
--    Es la comprobación de que el backfill y el escritor dicen lo mismo.
SELECT o.game_offer_id, o.game_id AS offer_game_id, g.game_id AS steam_game_game_id,
       o.region AS offer_region, g.region AS steam_game_region
FROM public.game_offers o
JOIN public.steam_games g ON g.steam_game_id = o.steam_game_id
WHERE o.game_id IS DISTINCT FROM g.game_id
   OR o.region IS DISTINCT FROM g.region;

-- 3. Ninguna oferta anclada a un juego que no existe. El FK lo impide, así que una fila aquí significa
--    que el constraint no existe y el DELETE de una fusión ya dejó basura.
SELECT o.game_offer_id, o.game_id
FROM public.game_offers o
WHERE o.game_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM public.games g WHERE g.game_id = o.game_id);

-- 4. Ningún duplicado bajo la clave canónica. El índice único lo impide: si esta consulta devuelve algo,
--    el índice no se creó.
SELECT game_id, region, source, offer_key, count(*) AS filas
FROM public.game_offers
GROUP BY game_id, region, source, offer_key
HAVING count(*) > 1;

-- 5. Cuántas ofertas siguen sin identidad canónica. NO es un error: es la condición que hay que ver a 0
--    antes de poder dropear uq_game_offers. Comentada a propósito: devuelve filas mientras haya
--    steam_games sin game_id, y este archivo exige 0 filas en cada consulta.
-- SELECT count(*) FROM public.game_offers WHERE game_id IS NULL;

-- 6. Ninguna oferta de tienda directa (source = 'epic' Fase 1, source = 'microsoft' Fase 2) convertida por
--    FX. Esas tiendas responden en la moneda de la región, así que su fila tiene que ser regional y su precio
--    MXN idéntico al original. Una fila aquí significa que se escribió una conversión sobre un precio que
--    ya era MXN, o al revés.
SELECT game_offer_id, source, original_currency, pricing_type,
       original_current_price_minor, mxn_current_price_minor, fx_rate
FROM public.game_offers
WHERE source IN ('epic', 'microsoft')
  AND (original_currency <> 'MXN'
       OR pricing_type <> 'regional'
       OR mxn_current_price_minor IS DISTINCT FROM original_current_price_minor
       OR mxn_regular_price_minor IS DISTINCT FROM original_regular_price_minor
       OR fx_rate IS NOT NULL);

-- 7. Ninguna oferta de Xbox (source = 'microsoft') sin ancla canónica. Estas filas no tienen por qué tener
--    snapshot de Steam, y por eso steam_game_id es NULL; pero sin game_id no hay nada que las encuentre, ni
--    el detalle (que lee por game_id) ni una futura página canónica. Una fila aquí es una oferta huérfana.
SELECT game_offer_id, steam_game_id, region
FROM public.game_offers
WHERE source = 'microsoft'
  AND game_id IS NULL;

-- 8. Toda oferta con steam_game_id NULL tiene que ser de Xbox. El único escritor de una oferta sin snapshot
--    es el pase de precios de tienda (source = 'microsoft'); ITAD y gg.deals se descubren por el snapshot de
--    Steam, así que una sin él es un dato que nadie refresca ni borra.
SELECT game_offer_id, source, offer_key
FROM public.game_offers
WHERE steam_game_id IS NULL
  AND source <> 'microsoft';
