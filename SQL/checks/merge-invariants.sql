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
