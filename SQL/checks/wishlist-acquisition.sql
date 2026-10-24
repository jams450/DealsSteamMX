-- Invariantes de la adquisición wishlist → biblioteca (odd/tasks/wishlist-library-acquisition.md).
-- Esperado: 0 filas en CADA consulta. Solo lectura: no modifica nada.
-- Correr tras aplicar SQL/migrations/2026-10-18_wishlist_library_acquisition.sql y
-- SQL/migrations/2026-10-19_wishlist_acquisition_source.sql, y después de cualquier adquisición o sync de
-- wishlist que se quiera auditar.

-- 1. Ningún tipo de adquisición fuera del vocabulario. El CHECK lo impide: si esta consulta devuelve algo,
--    la restricción no se aplicó y la columna ya acepta valores que nadie puede clasificar.
SELECT user_library_id, acquisition_type
FROM public.user_library
WHERE acquisition_type IS NOT NULL
  AND acquisition_type NOT IN ('purchase', 'gift', 'free', 'bundle', 'subscription');

-- 2. Ningún metadato parcial: tipo sin tienda, o fecha sin tipo. Son escrituras a medias, no estados.
SELECT user_library_id, acquired_store, acquisition_type, acquisition_date
FROM public.user_library
WHERE (acquisition_type IS NOT NULL AND acquired_store IS NULL)
   OR (acquisition_date IS NOT NULL AND acquisition_type IS NULL);

-- 3. Ninguna fila de wishlist con metadatos de adquisición. Los metadatos describen una compra, y una fila
--    `wished` no está comprada: si aparece una, algo escribió en la fila equivocada.
SELECT user_library_id, acquisition_type
FROM public.user_library
WHERE state = 'wished'
  AND (acquisition_type IS NOT NULL OR acquired_store IS NOT NULL OR acquisition_date IS NOT NULL
       OR acquired_store_type IS NOT NULL OR acquisition_provider IS NOT NULL);

-- 4. Un tipo `subscription` es un estado `subscription`, y un estado `subscription` con metadatos no puede
--    llevar otro tipo. Es el mapeo que implementa WishlistAcquisitionRules.StateFor; una fila aquí significa
--    que algo escribió la fila por fuera de esa regla.
SELECT user_library_id, state, acquisition_type
FROM public.user_library
WHERE (acquisition_type = 'subscription' AND state <> 'subscription')
   OR (state = 'subscription' AND acquisition_type IS NOT NULL AND acquisition_type <> 'subscription');

-- 5. Ninguna exclusión duplicada. El índice único lo impide: si devuelve filas, el índice no existe y el
--    sync leería dos veces la misma supresión.SELECT user_id, store, store_game_id, count(*) AS filas
FROM public.wishlist_exclusions
GROUP BY user_id, store, store_game_id
HAVING count(*) > 1;

-- 6. Ninguna exclusión sin usuario. El FK lo impide; una fila aquí significa que el constraint no existe y
--    quedó basura tras borrar un usuario.
SELECT e.wishlist_exclusion_id, e.user_id
FROM public.wishlist_exclusions e
WHERE NOT EXISTS (SELECT 1 FROM public.users u WHERE u.user_id = e.user_id);

-- 7. Ninguna exclusión conviviendo con la fila de wishlist que debía suprimir. La adquisición que elige
--    quitarlo borra esa fila en la misma transacción y la exclusión evita que el sync la reescriba; las dos
--    juntas significan que el borrado no ocurrió y la persona seguiría viendo el juego en la wishlist.
SELECT e.user_id, e.store, e.store_game_id, l.user_library_id
FROM public.wishlist_exclusions e
JOIN public.user_library l
  ON l.user_id = e.user_id
 AND l.store = e.store
 AND l.store_game_id = e.store_game_id
WHERE l.state = 'wished';

-- 8. Ninguna fila de categoría colgando de una fila de biblioteca que ya no existe (el DELETE de la
--    adquisición arrastra las asignaciones por fk_wishlist_category_items_library_user). Si devuelve filas,
--    el FK no está y la limpieza de categorías de la que depende la adquisición no ocurrió.
SELECT i.wishlist_category_id, i.user_id, i.user_library_id
FROM public.wishlist_category_items i
WHERE NOT EXISTS (
    SELECT 1 FROM public.user_library l
    WHERE l.user_id = i.user_id AND l.user_library_id = i.user_library_id);

-- 9. Ninguna clasificación de tienda fuera del vocabulario (official/keyshop/unknown). El CHECK
--    ck_user_library_acquired_store_type lo impide; si devuelve filas, la migración 2026-10-19 no se aplicó y
--    la columna acepta valores que la UI nunca ofrece.
SELECT user_library_id, acquired_store_type
FROM public.user_library
WHERE acquired_store_type IS NOT NULL
  AND acquired_store_type NOT IN ('official', 'keyshop', 'unknown');

-- 10. Ningún proveedor de descubrimiento fuera del vocabulario (itad/ggdeals/direct/other). El CHECK
--     ck_user_library_acquisition_provider lo impide.
SELECT user_library_id, acquisition_provider
FROM public.user_library
WHERE acquisition_provider IS NOT NULL
  AND acquisition_provider NOT IN ('itad', 'ggdeals', 'direct', 'other');

-- 11. Ningún hecho de origen sin la adquisición que describe: una clasificación o un proveedor en una fila
--     sin tienda y tipo de adquisición es una escritura parcial. ck_user_library_acquisition_source lo
--     impide; si devuelve filas, esa restricción falta y una repetición podría rellenar una compra ya
--     registrada con datos de un segundo envío.
SELECT user_library_id, acquired_store, acquisition_type, acquired_store_type, acquisition_provider
FROM public.user_library
WHERE (acquired_store_type IS NOT NULL OR acquisition_provider IS NOT NULL)
  AND (acquisition_type IS NULL OR acquired_store IS NULL);
