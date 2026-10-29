-- Annual tiers keyed by the reviewed play, not by the game (docs/2026-10-20_REVIEW_TIERS.md, specs S5-S6).
--
-- La tabla vieja tenía PK (user_id, year, game_id), así que un juego jugado dos veces en el mismo año
-- — incluso dos veces en la misma plataforma — solo podía tener un puesto: la segunda partida no existía
-- para la tier list. La identidad nueva es la reseña (`game_review_id`); `game_id` se queda como ancla de
-- fusión, y SQL/checks/game_tiers_reviews.sql comprueba que sigue coincidiendo con su reseña.
--
-- Despliegue: aplicar esta migración ANTES de desplegar API y frontend. El binario viejo no funciona
-- contra el esquema nuevo (inserta sin game_review_id) ni el nuevo contra el viejo (su PK no coincide),
-- así que no hay convivencia: migrar y desplegar seguido, con respaldo previo de user_game_tiers.
-- Rollback: no existe downgrade sin pérdida. Colapsar la clave nueva a (user_id, year, game_id) obliga a
-- elegir una tarjeta por juego y año y borrar las demás; la copia no guarda cuál era el puesto original.
-- El rollback real es restaurar el respaldo (docs/2026-10-20_REVIEW_TIERS.md, «Migración manual»).
--
-- Backfill NO destructivo. El diseño descartado era "el puesto viejo se asigna a la reseña más nueva",
-- que borra el puesto de las otras partidas del mismo juego y año. Aquí cada puesto viejo se COPIA con su
-- rango y posición a todas las reseñas fechadas que le corresponden, y después cada tarjeta se mueve de
-- forma independiente desde la UI.
--
-- Solo se borra un puesto cuando no tiene ninguna reseña fechada que lo respalde (la reseña se eliminó o
-- se le cambió el año antes de esta migración). Un puesto con reseña nunca se pierde.
--
-- Comprobaciones de lectura antes de aplicar, con su control positivo al lado:
--   -- 1. Filas que existen hoy (control positivo: debe ser > 0 en una base con tier list usada).
--   SELECT count(*) FROM public.user_game_tiers;
--   -- 2. Tarjetas que producirá la copia. Debe ser >= (1) y crecer solo por partidas repetidas.
--   SELECT count(*)
--   FROM public.user_game_tiers t
--   JOIN public.game_reviews r
--     ON r.user_id = t.user_id AND r.game_id = t.game_id
--    AND date_part('year', COALESCE(r.finished_month, r.started_month)) = t.year;
--   -- 3. Huérfanos que se van a borrar (sin reseña fechada que los respalde). Si sale > 0, guarda el
--   --    resultado: esos puestos desaparecen a propósito y no hay rollback que los traiga de vuelta.
--   SELECT t.user_id, t.year, t.game_id, t.tier, t.position
--   FROM public.user_game_tiers t
--   WHERE NOT EXISTS (
--       SELECT 1 FROM public.game_reviews r
--       WHERE r.user_id = t.user_id AND r.game_id = t.game_id
--         AND date_part('year', COALESCE(r.finished_month, r.started_month)) = t.year);
--
-- Idempotente: se puede volver a correr sin duplicar nada (la copia solo mira filas con
-- game_review_id NULL y las constraints se recrean con IF EXISTS / IF NOT EXISTS).

BEGIN;

-- 1. La PK vieja prohíbe que la copia cree una segunda tarjeta del mismo juego en el mismo año.
ALTER TABLE public.user_game_tiers DROP CONSTRAINT IF EXISTS user_game_tiers_pkey;

-- 2. Columna nueva, nullable mientras se copia.
ALTER TABLE public.user_game_tiers ADD COLUMN IF NOT EXISTS game_review_id BIGINT;

-- 3. Backfill: cada puesto viejo se replica a TODAS las reseñas fechadas del mismo usuario, juego y año.
--    El JOIN no usa DISTINCT ON a propósito: elegir una sola reseña sería el backfill con pérdida.
INSERT INTO public.user_game_tiers
    (user_id, year, game_review_id, game_id, tier, position, created_at, updated_at, created_by, updated_by)
SELECT t.user_id,
       t.year,
       r.game_review_id,
       t.game_id,
       t.tier,
       t.position,
       t.created_at,
       t.updated_at,
       t.created_by,
       t.updated_by
FROM public.user_game_tiers t
JOIN public.game_reviews r
  ON r.user_id = t.user_id
 AND r.game_id = t.game_id
 AND date_part('year', COALESCE(r.finished_month, r.started_month)) = t.year
WHERE t.game_review_id IS NULL;

-- 4. Solo huérfanos: los puestos sin reseña fechada (y las filas viejas que la copia ya reemplazó).
DELETE FROM public.user_game_tiers WHERE game_review_id IS NULL;

-- 5. Cerrar la forma nueva. El CASCADE importa: borrar una reseña tiene que borrar su tarjeta, igual que
--    ya pasa con el juego.
ALTER TABLE public.user_game_tiers ALTER COLUMN game_review_id SET NOT NULL;

ALTER TABLE public.user_game_tiers DROP CONSTRAINT IF EXISTS user_game_tiers_game_review_id_fkey;
ALTER TABLE public.user_game_tiers
    ADD CONSTRAINT user_game_tiers_game_review_id_fkey
    FOREIGN KEY (game_review_id) REFERENCES public.game_reviews(game_review_id) ON DELETE CASCADE;

ALTER TABLE public.user_game_tiers
    ADD CONSTRAINT user_game_tiers_pkey PRIMARY KEY (user_id, year, game_review_id);

-- 6. Índices de los dos FK en CASCADE: el de games lo recorre cada fusión, el de game_reviews cada
--    borrado de reseña.
CREATE INDEX IF NOT EXISTS idx_user_game_tiers_game ON public.user_game_tiers(game_id);
CREATE INDEX IF NOT EXISTS idx_user_game_tiers_review ON public.user_game_tiers(game_review_id);

COMMIT;

ANALYZE public.user_game_tiers;
