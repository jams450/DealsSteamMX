-- Estado de partida en las reseñas + favoritos del usuario.
--
-- status: cómo terminó esa partida. `finished` (terminado), `completed` (100%) y `dropped` (dropeado,
-- abandonado). Es NOT NULL con default `finished` para que las reseñas existentes queden clasificadas sin
-- backfill: una reseña escrita antes de este cambio se leyó como "terminada". "Por jugar" NO es un estado
-- guardado: es la ausencia de reseña, y se deriva en la lectura.
--
-- user_game_favorites: marca de favorito por (usuario, juego). Tabla propia y no una columna de
-- game_reviews porque un favorito es del juego, no de la partida: con varias reseñas no habría forma de
-- saber cuál manda. Una fila presente = favorito; desmarcar borra la fila.

ALTER TABLE public.game_reviews
    ADD COLUMN IF NOT EXISTS status VARCHAR(16) NOT NULL DEFAULT 'finished';

CREATE TABLE IF NOT EXISTS public.user_game_favorites (
    user_id INT NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    game_id BIGINT NOT NULL REFERENCES public.games(game_id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    PRIMARY KEY (user_id, game_id)
);

CREATE INDEX IF NOT EXISTS idx_user_game_favorites_game ON public.user_game_favorites(game_id);

ANALYZE public.game_reviews;
ANALYZE public.user_game_favorites;
