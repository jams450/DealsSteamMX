-- Invariantes de la compra de bundles (odd/tasks/bundle-expiry-acquisition.md S3-S6).
-- Esperado: 0 filas en CADA consulta. Correr después de aplicar
-- SQL/migrations/2026-10-21_bundle_purchases.sql y tras cualquier fusión de juegos.
-- Solo lectura: no modifica nada.

-- 1. Membresía huérfana. El FK lo impide, así que una fila aquí significa que el constraint no existe y una
--    fusión ya dejó evidencia apuntando a un juego borrado.
SELECT g.bundle_purchase_game_id, g.game_id
FROM public.bundle_purchase_games g
WHERE NOT EXISTS (SELECT 1 FROM public.games j WHERE j.game_id = g.game_id);

-- 2. Compra sin membresía. selected_count > 0 y la importación escribe una fila por juego: cero filas
--    significa que una compra quedó a medias y el inventario miente.
SELECT p.bundle_purchase_id, p.selected_count
FROM public.bundle_purchases p
WHERE NOT EXISTS (
    SELECT 1 FROM public.bundle_purchase_games g WHERE g.bundle_purchase_id = p.bundle_purchase_id
);

-- 3. selected_count no coincide con la membresía realmente registrada.
SELECT p.bundle_purchase_id, p.selected_count, count(g.bundle_purchase_game_id) AS filas
FROM public.bundle_purchases p
JOIN public.bundle_purchase_games g ON g.bundle_purchase_id = p.bundle_purchase_id
GROUP BY p.bundle_purchase_id, p.selected_count
HAVING count(g.bundle_purchase_game_id) <> p.selected_count;

-- 4. Un tier fijo debe registrar toda su membresía elegible; un tier de elección, exactamente el conteo
--    publicado. Un desvío significa que el servidor importó una selección distinta a la validada.
SELECT p.bundle_purchase_id, p.selection_mode, p.tier_selection_count, p.selected_count
FROM public.bundle_purchases p
WHERE (p.selection_mode = 'fixed' AND p.tier_selection_count IS NOT NULL)
   OR (p.selection_mode = 'choice' AND (p.tier_selection_count IS NULL OR p.selected_count <> p.tier_selection_count));

-- 5. Dos imports del mismo juego en la misma plataforma dentro de una compra. El servicio decide duplicado
--    contra las filas ya escritas, así que una fila aquí significa que la deduplicación se rompió y la
--    biblioteca recibió el mismo juego dos veces.
SELECT g.bundle_purchase_id, g.platform, g.game_id, count(*) AS imports
FROM public.bundle_purchase_games g
WHERE g.outcome = 'imported'
GROUP BY g.bundle_purchase_id, g.platform, g.game_id
HAVING count(*) > 1;

-- 6. Un import que apunta a una fila de biblioteca inexistente. NULL es válido después de un borrado
--    (el FK es ON DELETE SET NULL); un id que ya no resuelve significa que el FK no existe.
SELECT g.bundle_purchase_game_id, g.bundle_purchase_id, g.outcome, g.user_library_id
FROM public.bundle_purchase_games g
WHERE g.outcome = 'imported'
  AND g.user_library_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM public.user_library ul WHERE ul.user_library_id = g.user_library_id);

-- 7. La fila de biblioteca de un import no coincide ni en juego ni en plataforma con la membresía. Es la
--    comprobación de que el snapshot y el import dicen lo mismo.
SELECT g.bundle_purchase_game_id, g.game_id, ul.game_id AS library_game_id,
       g.platform, ul.store AS library_store
FROM public.bundle_purchase_games g
JOIN public.user_library ul ON ul.user_library_id = g.user_library_id
WHERE g.outcome = 'imported'
  AND (ul.game_id IS DISTINCT FROM g.game_id OR ul.store IS DISTINCT FROM g.platform);

-- 8. Un juego marcado como quitado de la wishlist sin la exclusión que impide que el sync lo readmita.
--    La exclusión es la única protección local, así que su ausencia reabre exactamente el bug de S5. El
--    appid puede venir del mapeo canónico (game_external_ids) o del snapshot de Steam, igual que en el
--    servicio: se acepta cualquiera de los dos, porque inventar uno solo sería más estricto que el código.
SELECT g.bundle_purchase_game_id, g.title
FROM public.bundle_purchase_games g
JOIN public.bundle_purchases p ON p.bundle_purchase_id = g.bundle_purchase_id
WHERE g.wishlist_removed
  AND NOT EXISTS (
      SELECT 1
      FROM public.wishlist_exclusions x
      WHERE x.user_id = p.user_id
        AND x.store = 'steam'
        AND (
            EXISTS (
                SELECT 1 FROM public.game_external_ids e
                WHERE e.game_id = g.game_id
                  AND e.namespace = 'steam'
                  AND e.external_id = x.store_game_id)
            OR EXISTS (
                SELECT 1 FROM public.steam_games sg
                WHERE sg.game_id = g.game_id
                  AND sg.app_id::text = x.store_game_id)));

-- 9. Snapshot vacío o hash en blanco: la compra dejaría de probar qué tier se compró y el replay no podría
--    comparar nada.
SELECT p.bundle_purchase_id
FROM public.bundle_purchases p
WHERE length(btrim(p.tier_key)) = 0
   OR length(btrim(p.snapshot_hash)) = 0
   OR p.snapshot_json IS NULL
   OR p.snapshot_json::text = 'null';
