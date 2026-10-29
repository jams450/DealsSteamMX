-- Invariantes de identidad canónica. Esperado: 0 filas en CADA consulta.
-- Correr tras cualquier operación que cree o reasigne identidad (import de biblioteca, fusión de juegos).
-- Solo lectura: no modifica nada.

-- 1. Ningún juego canónico con más de un appid de Steam.
--    Dos appids en un mismo game_id hacen que el binding de precio elija "el primero" sin orden
--    determinista, así que el precio dejaría de ser reproducible.
SELECT game_id, count(DISTINCT external_id) AS steam_ids
FROM public.game_external_ids
WHERE namespace = 'steam'
GROUP BY game_id
HAVING count(DISTINCT external_id) > 1;

-- 2. Ningún juego canónico huérfano (sin ninguna identidad externa).
SELECT g.game_id
FROM public.games g
WHERE NOT EXISTS (SELECT 1 FROM public.game_external_ids e WHERE e.game_id = g.game_id);

-- 3. Ninguna fusión registrada sobre sí misma.
SELECT game_merge_id
FROM public.game_merges
WHERE absorbed_game_id = survivor_game_id;

-- 4. Ningún superviviente de fusión que ya no exista.
SELECT m.game_merge_id
FROM public.game_merges m
WHERE NOT EXISTS (SELECT 1 FROM public.games g WHERE g.game_id = m.survivor_game_id);

-- 5. Ningún puesto de tier list huérfano: un puesto existe solo si el usuario tiene una reseña fechada
--    ese año sobre el mismo juego (el año del puesto es Fin, o Inicio si no hay Fin).
--    Importa después de fusionar: `user_game_tiers.game_id` es CASCADE, así que una fusión que no la
--    repunta borra el puesto sin error, y una que la repunta mal deja el puesto apuntando a un juego
--    que ese año no fue reseñado (la fila queda invisible en la UI, pero viva en la BD).
SELECT t.user_id, t.year, t.game_id
FROM public.user_game_tiers t
WHERE NOT EXISTS (
    SELECT 1 FROM public.game_reviews r
    WHERE r.user_id = t.user_id AND r.game_id = t.game_id
      AND COALESCE(r.finished_month, r.started_month) IS NOT NULL
      AND EXTRACT(YEAR FROM COALESCE(r.finished_month, r.started_month)) = t.year);

-- 6. El historial de compra mantiene un ancla canónica existente después de una fusión.

SELECT p.bundle_purchase_game_id, p.game_id

FROM public.bundle_purchase_games p

WHERE NOT EXISTS (SELECT 1 FROM public.games g WHERE g.game_id = p.game_id);
