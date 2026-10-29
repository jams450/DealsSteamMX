-- Bundle acquisition (odd/tasks/bundle-expiry-acquisition.md S3-S6): compra reportada por el usuario,
-- snapshot del tier comprado y membresía importada/duplicada. Aplicar después de
-- 2026-10-20_user_game_tiers_per_review.sql. Manual y de una sola pasada: CREATE TABLE / INDEX van con
-- IF NOT EXISTS y los CHECK se agregan con guarda, porque Postgres no tiene ADD CONSTRAINT IF NOT EXISTS.
--
-- 1) bundle_purchases
--    Una fila por (user, source, bundle_key). Es el registro de adquisición y la clave de idempotencia:
--    un segundo envío del mismo bundle contesta desde esta fila en vez de volver a importar. Guarda el
--    snapshot del proveedor (snapshot_json + snapshot_hash) y los hechos del tier comprado, así que la
--    membresía comprada sigue siendo legible aunque external_bundles.tiers_json se reescriba después.
--    Sin FK a external_bundles: la compra es evidencia y sobrevive a una purga del proveedor.
--
-- 2) bundle_purchase_games
--    Una fila por ítem seleccionado: el juego canónico resuelto, la plataforma y si se importó o fue
--    duplicado. Una fila 'duplicate' no se omite: alimenta el inventario de duplicados (uno por juego
--    comprado) y demuestra que el bundle sí incluía ese juego. game_id es NO ACTION: una fusión lo repunta
--    (GameMergeService) y nunca borra la evidencia por cascada. user_library_id es SET NULL porque el
--    puntero a la fila de biblioteca es una comodidad, no la identidad de la compra.

CREATE TABLE IF NOT EXISTS public.bundle_purchases (
    bundle_purchase_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    source VARCHAR(16) NOT NULL,
    bundle_key VARCHAR(128) NOT NULL,
    title VARCHAR(512) NOT NULL,
    shop_id VARCHAR(32),
    shop_name VARCHAR(128),
    page_url VARCHAR(1024),
    deal_url VARCHAR(1024),
    published_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ,
    observed_at TIMESTAMPTZ NOT NULL,
    tier_key VARCHAR(64) NOT NULL,
    tier_index INT NOT NULL,
    tier_price_minor INT,
    tier_currency VARCHAR(3),
    tier_addon BOOLEAN NOT NULL DEFAULT FALSE,
    tier_items_complete BOOLEAN NOT NULL DEFAULT FALSE,
    tier_selection_count INT,
    selection_mode VARCHAR(8) NOT NULL,
    selected_count INT NOT NULL,
    platform VARCHAR(32) NOT NULL,
    acquired_at DATE,
    acquired_store VARCHAR(32),
    acquired_store_type VARCHAR(16),
    acquisition_provider VARCHAR(16),
    snapshot_hash VARCHAR(64) NOT NULL,
    snapshot_json JSONB NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_bundle_purchases UNIQUE (user_id, source, bundle_key)
);

-- Sin índice aparte por user_id: uq_bundle_purchases ya empieza por user_id y sirve la lectura del
-- inventario y la búsqueda de la compra que oculta un bundle del catálogo pendiente.

CREATE TABLE IF NOT EXISTS public.bundle_purchase_games (
    bundle_purchase_game_id BIGSERIAL PRIMARY KEY,
    bundle_purchase_id BIGINT NOT NULL REFERENCES public.bundle_purchases(bundle_purchase_id) ON DELETE CASCADE,
    item_key VARCHAR(128) NOT NULL,
    title VARCHAR(256) NOT NULL,
    item_type VARCHAR(32),
    itad_game_id VARCHAR(36),
    game_id BIGINT NOT NULL REFERENCES public.games(game_id),
    canonical_source VARCHAR(8) NOT NULL,
    platform VARCHAR(32) NOT NULL,
    outcome VARCHAR(16) NOT NULL,
    user_library_id BIGINT REFERENCES public.user_library(user_library_id) ON DELETE SET NULL,
    wishlist_removed BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_bundle_purchase_games UNIQUE (bundle_purchase_id, item_key)
);

CREATE INDEX IF NOT EXISTS idx_bundle_purchase_games_game
    ON public.bundle_purchase_games(game_id);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_selection_mode'
    ) THEN
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_selection_mode
            CHECK (selection_mode IN ('fixed', 'choice'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_selection_count'
    ) THEN
        -- Un tier fijo no tiene conteo de selección; uno de elección lo exige.
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_selection_count CHECK (
                (selection_mode = 'fixed' AND tier_selection_count IS NULL)
                OR (selection_mode = 'choice' AND tier_selection_count IS NOT NULL));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_selected_count'
    ) THEN
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_selected_count CHECK (selected_count > 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_tier_key_nonblank'
    ) THEN
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_tier_key_nonblank
            CHECK (length(btrim(tier_key)) > 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_snapshot_hash_nonblank'
    ) THEN
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_snapshot_hash_nonblank
            CHECK (length(btrim(snapshot_hash)) > 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_platform_nonblank'
    ) THEN
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_platform_nonblank
            CHECK (length(btrim(platform)) > 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_acquired_store_type'
    ) THEN
        -- Vocabulario cerrado, espejo de WishlistAcquiredStoreTypes en C#.
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_acquired_store_type CHECK (
                acquired_store_type IS NULL
                OR acquired_store_type IN ('official', 'keyshop', 'unknown'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_acquisition_provider'
    ) THEN
        -- Vocabulario cerrado, espejo de WishlistAcquisitionProviders en C#.
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_acquisition_provider CHECK (
                acquisition_provider IS NULL
                OR acquisition_provider IN ('itad', 'ggdeals', 'direct', 'other'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchases_acquisition_source'
    ) THEN
        ALTER TABLE public.bundle_purchases
            ADD CONSTRAINT ck_bundle_purchases_acquisition_source CHECK (
                (acquired_store_type IS NULL AND acquisition_provider IS NULL)
                OR acquired_store IS NOT NULL);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchase_games_canonical_source'
    ) THEN
        ALTER TABLE public.bundle_purchase_games
            ADD CONSTRAINT ck_bundle_purchase_games_canonical_source
            CHECK (canonical_source IN ('itad', 'manual'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchase_games_outcome'
    ) THEN
        ALTER TABLE public.bundle_purchase_games
            ADD CONSTRAINT ck_bundle_purchase_games_outcome
            CHECK (outcome IN ('imported', 'duplicate'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchase_games_platform_nonblank'
    ) THEN
        ALTER TABLE public.bundle_purchase_games
            ADD CONSTRAINT ck_bundle_purchase_games_platform_nonblank
            CHECK (length(btrim(platform)) > 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_bundle_purchase_games_item_key_nonblank'
    ) THEN
        ALTER TABLE public.bundle_purchase_games
            ADD CONSTRAINT ck_bundle_purchase_games_item_key_nonblank
            CHECK (length(btrim(item_key)) > 0);
    END IF;
END $$;

ANALYZE public.bundle_purchases;
ANALYZE public.bundle_purchase_games;
