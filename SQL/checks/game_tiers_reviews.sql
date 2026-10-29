-- Invariantes del puesto anual atado a su reseña (docs/2026-10-20_REVIEW_TIERS.md, specs S5-S6).
-- Esperado: 0 filas en CADA consulta. Correr después de aplicar
-- SQL/migrations/2026-10-20_user_game_tiers_per_review.sql, después de cualquier fusión de juegos y
-- después de borrar reseñas. Solo lectura: no modifica nada.

-- 1. El puesto pertenece al dueño de su reseña. La identidad de la tarjeta es la partida reseñada, así
--    que un puesto con la reseña de otra persona sería una escritura cruzada entre usuarios.
SELECT t.user_id AS tier_user_id, t.year, t.game_review_id, r.user_id AS review_user_id
FROM public.user_game_tiers t
LEFT JOIN public.game_reviews r ON r.game_review_id = t.game_review_id
WHERE r.game_review_id IS NULL
   OR r.user_id <> t.user_id;

-- 2. El ancla de fusión coincide con su reseña. GameMergeService repunta game_id y las reseñas en la
--    misma transacción; una fila aquí significa que una fusión movió una mitad y no la otra.
SELECT t.user_id, t.year, t.game_review_id, t.game_id AS tier_game_id, r.game_id AS review_game_id
FROM public.user_game_tiers t
JOIN public.game_reviews r ON r.game_review_id = t.game_review_id
WHERE t.game_id <> r.game_id;

-- 3. Ningún puesto sin reseña. La columna es NOT NULL con FK en CASCADE: una fila aquí significa que la
--    migración no se aplicó (o que se recreó la tabla con la forma vieja).
SELECT t.user_id, t.year, t.game_id
FROM public.user_game_tiers t
WHERE t.game_review_id IS NULL;

-- 4. Un puesto por (usuario, año, reseña). La PK lo impide: si esto devuelve algo, la PK no existe y la
--    copia de la migración pudo duplicar tarjetas.
SELECT t.user_id, t.year, t.game_review_id, count(*) AS filas
FROM public.user_game_tiers t
GROUP BY t.user_id, t.year, t.game_review_id
HAVING count(*) > 1;

-- 5. Año del puesto. NO es un invariante y por eso no hay consulta obligatoria: el año se deriva de
--    `finished_month ?? started_month`, y una reseña puede editarse a otro año después de guardar su
--    tarjeta. El puesto queda en el año viejo, invisible para la lista (GetForYear filtra por el año de la
--    reseña) pero preservado: mover datos de vuelta requeriría adivinar la intención del usuario.
--    Un CHECK o un trigger no puede expresar esa derivación sin prohibir la edición legítima de la fecha,
--    así que no se inventa una constraint que el ciclo de vida no sostiene. Diagnóstico, si se quiere
--    revisar la deriva acumulada (devuelve filas a propósito, no se corre como invariante):
-- SELECT t.user_id, t.year, t.game_review_id,
--        date_part('year', COALESCE(r.finished_month, r.started_month))::int AS review_year
-- FROM public.user_game_tiers t
-- JOIN public.game_reviews r ON r.game_review_id = t.game_review_id
-- WHERE (r.finished_month IS NULL AND r.started_month IS NULL)
--    OR date_part('year', COALESCE(r.finished_month, r.started_month)) <> t.year;
