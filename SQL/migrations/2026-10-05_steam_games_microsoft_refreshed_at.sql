-- Microsoft Store refresh timestamp (docs/PLAN_MULTISTORE.md §6, Fase 2, camino por StoreId).
--
-- El precio de Microsoft es una fila normal de game_offers con source = 'microsoft' y offer_key = 'store'
-- (la tienda vende una oferta base por juego), así que no hacen falta columnas de precio por tienda: solo
-- el gate que decide cuándo volver a llamar. Independiente de offers_refreshed_at, ggdeals_refreshed_at,
-- bundles_refreshed_at y epic_refreshed_at: un fallo de Microsoft no puede marcar como fresco el snapshot
-- de otro proveedor, ni al revés.
--
-- La columna existe además porque este camino no depende de la biblioteca: la oferta de un juego que el
-- usuario no tiene en Xbox se escribe aquí, y su frescura tiene que poder leerse aunque el juego no tenga
-- fila en user_library.

ALTER TABLE public.steam_games
    ADD COLUMN IF NOT EXISTS microsoft_refreshed_at TIMESTAMPTZ;

ANALYZE public.steam_games;
