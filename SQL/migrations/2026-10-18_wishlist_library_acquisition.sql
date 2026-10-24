-- Wishlist → biblioteca: metadatos de adquisición + exclusiones locales de la wishlist.
-- Spec: odd/tasks/wishlist-library-acquisition.md (S2/S3). Aplicar después de
-- 2026-10-17_ntfy_deal_alerts.sql. Manual y de una sola pasada: los ADD COLUMN / CREATE TABLE / índice van
-- con IF NOT EXISTS para que reejecutarlo no falle; el CHECK se agrega con guarda porque no existe
-- ADD CONSTRAINT IF NOT EXISTS.
--
-- 1) user_library.acquired_store / acquisition_type / acquisition_date
--    Metadatos de la adquisición, nullable como grupo: NULL = "no registrado", que es toda fila importada
--    (Playnite, consola, sync de wishlist). Por eso el import sigue escribiendo exactamente lo mismo.
--    acquired_store NO es la tienda de la fila: la fila vive en la biblioteca de la plataforma elegida
--    (user_library.store) y acquired_store dice dónde se compró. Una suscripción escribe
--    state = 'subscription'; los otros cuatro tipos escriben 'owned'.
--
-- 2) wishlist_exclusions
--    Una fila por (user_id, store, store_game_id) suprimido. La fila ES la supresión; borrarla es un
--    DELETE. El sync de la wishlist la lee antes de escribir, así que un juego adquirido y quitado de la
--    wishlist no vuelve en el siguiente sync de Steam. No es un user_library.state nuevo: el sync borra
--    las filas que Steam ya no devuelve, así que un estado "descartado" habría que recrearlo en cada
--    corrida y cambiaría el significado de user_library para todos los demás lectores.
--    Sin game_id a propósito: el id de tienda es la identidad y una fusión no puede dejarla huérfana.

ALTER TABLE public.user_library
    ADD COLUMN IF NOT EXISTS acquired_store VARCHAR(32);

ALTER TABLE public.user_library
    ADD COLUMN IF NOT EXISTS acquisition_type VARCHAR(16);

ALTER TABLE public.user_library
    ADD COLUMN IF NOT EXISTS acquisition_date DATE;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_user_library_acquisition_type'
    ) THEN
        -- Vocabulario cerrado: un valor fuera de la lista sería una fila que nadie puede clasificar.
        ALTER TABLE public.user_library
            ADD CONSTRAINT ck_user_library_acquisition_type CHECK (
                acquisition_type IS NULL
                OR acquisition_type IN ('purchase', 'gift', 'free', 'bundle', 'subscription'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_user_library_acquisition_metadata'
    ) THEN
        -- Se escriben juntos o no se escriben: un tipo sin tienda (o una fecha sin tipo) es una escritura
        -- parcial, no un estado válido.
        ALTER TABLE public.user_library
            ADD CONSTRAINT ck_user_library_acquisition_metadata CHECK (
                (acquisition_type IS NULL AND acquired_store IS NULL AND acquisition_date IS NULL)
                OR (acquisition_type IS NOT NULL AND acquired_store IS NOT NULL));
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS public.wishlist_exclusions (
    wishlist_exclusion_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    store VARCHAR(32) NOT NULL,
    store_game_id VARCHAR(64) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_wishlist_exclusions UNIQUE (user_id, store, store_game_id),
    CONSTRAINT ck_wishlist_exclusions_store_nonblank CHECK (length(btrim(store)) > 0),
    CONSTRAINT ck_wishlist_exclusions_store_game_id_nonblank CHECK (length(btrim(store_game_id)) > 0)
);

-- Sin índice aparte por user_id: el índice de uq_wishlist_exclusions ya empieza por user_id, así que sirve
-- todas las lecturas por usuario y un segundo índice solo costaría escrituras.

-- El sync de la wishlist resuelve "¿este appid está excluido?" con un lookup por usuario, cubierto por la
-- clave única (user_id, store, store_game_id).
ANALYZE public.user_library;
ANALYZE public.wishlist_exclusions;
