-- Wishlist → biblioteca: clasificación de la tienda de adquisición + proveedor de descubrimiento.
-- Spec: odd/tasks/wishlist-library-acquisition.md (S6, decisión L14 del usuario: «cambia en tienda donde lo
-- adquiriste si fue por keyshop oficial o no y que proveedor si itad o ggdeal etc»). Aplicar después de
-- 2026-10-18_wishlist_library_acquisition.sql, que NO se reescribe: ya puede estar aplicada en algún
-- servidor. Manual y de una sola pasada; todo va con IF NOT EXISTS / guarda para que reejecutarla no falle.
--
-- 1) user_library.acquired_store_type
--    Clasificación de acquired_store: 'official' (tienda oficial/autorizada), 'keyshop' (revendedor de
--    claves / mercado gris) o 'unknown' (no clasificado). Es un hecho DISTINTO de la tienda: acquired_store
--    sigue diciendo dónde se compró, sin cambios. NULL = "no registrado", que es toda fila escrita antes de
--    esta columna y todo cliente que no manda el campo. Nunca se deduce de un precio ni de un proveedor.
--
-- 2) user_library.acquisition_provider
--    Dónde se descubrió la oferta que se compró: 'itad', 'ggdeals', 'direct' (la tienda misma) u 'other'.
--    Igual de opcional, y por la misma razón: es un dato que la persona registra, no una inferencia.
--
-- Ambas son nullable y aditivas: el import de Playnite, la consola y el sync de wishlist siguen escribiendo
-- exactamente lo mismo, y las filas existentes quedan válidas sin backfill (rellenarlas sería inventar la
-- clasificación de una compra pasada). Por eso no hay UPDATE en este archivo.

ALTER TABLE public.user_library
    ADD COLUMN IF NOT EXISTS acquired_store_type VARCHAR(16);

ALTER TABLE public.user_library
    ADD COLUMN IF NOT EXISTS acquisition_provider VARCHAR(16);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_user_library_acquired_store_type'
    ) THEN
        -- Vocabulario cerrado, espejo de WishlistAcquiredStoreTypes en C#: un valor fuera de la lista es una
        -- fila que nadie puede clasificar después.
        ALTER TABLE public.user_library
            ADD CONSTRAINT ck_user_library_acquired_store_type CHECK (
                acquired_store_type IS NULL
                OR acquired_store_type IN ('official', 'keyshop', 'unknown'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_user_library_acquisition_provider'
    ) THEN
        -- Vocabulario cerrado, espejo de WishlistAcquisitionProviders en C#.
        ALTER TABLE public.user_library
            ADD CONSTRAINT ck_user_library_acquisition_provider CHECK (
                acquisition_provider IS NULL
                OR acquisition_provider IN ('itad', 'ggdeals', 'direct', 'other'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'ck_user_library_acquisition_source'
    ) THEN
        -- Los dos hechos describen la tienda de una adquisición, así que no existen sin ella. Va como
        -- constraint aparte y no dentro de ck_user_library_acquisition_metadata porque ese CHECK ya está
        -- aplicado en las bases donde corrió la migración anterior, y una base migrada no puede quedar con
        -- una definición distinta de la de una instalación nueva.
        ALTER TABLE public.user_library
            ADD CONSTRAINT ck_user_library_acquisition_source CHECK (
                (acquired_store_type IS NULL AND acquisition_provider IS NULL)
                OR (acquisition_type IS NOT NULL AND acquired_store IS NOT NULL));
    END IF;
END $$;

-- Sin índice: no hay ninguna consulta que filtre ni ordene por estas dos columnas; solo se leen con la fila.
ANALYZE public.user_library;
