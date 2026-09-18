-- Idempotent canonical backfill (docs/PLAN_CATALOG.md §5, PLAN_IMPLEMENTACION_BIBLIOTECA.md §4).
-- No title-based merge anywhere: a row joins an existing games row only through an exact
-- (namespace, external_id) mapping. Re-running this file changes nothing.
--
-- normalized_title here is a plain lowercase/punctuation-stripped approximation of the C# normalizer
-- (GameTitleNormalizer); it is a comparison key only, no identity decision reads it. Edition-token
-- stripping lives in the runtime resolver and is not reproduced in SQL on purpose.

-- 1. One canonical game per distinct Steam appid with no ('steam', appid) mapping yet. The regional
--    row chosen as source is deterministic (MIN(steam_game_id)); every region of the app then shares it.
DO $$
DECLARE
    r RECORD;
    new_game_id BIGINT;
BEGIN
    FOR r IN
        SELECT DISTINCT ON (sg.app_id) sg.app_id, sg.name, sg.type, sg.image_url, sg.is_free
        FROM public.steam_games sg
        WHERE NOT EXISTS (
            SELECT 1 FROM public.game_external_ids e
            WHERE e.namespace = 'steam' AND e.external_id = sg.app_id::text
        )
        ORDER BY sg.app_id, sg.steam_game_id
    LOOP
        INSERT INTO public.games (title, normalized_title, type, image_url, is_free, created_at, updated_at)
        VALUES (
            r.name,
            btrim(regexp_replace(translate(lower(r.name), 'áàâäãåéèêëíìîïóòôöõúùûüñç', 'aaaaaaeeeeiiiiooooouuuunc'), '[^a-z0-9]+', ' ', 'g')),
            r.type,
            r.image_url,
            r.is_free,
            NOW(),
            NOW()
        )
        RETURNING game_id INTO new_game_id;

        INSERT INTO public.game_external_ids (game_id, namespace, external_id, created_at, updated_at)
        VALUES (new_game_id, 'steam', r.app_id::text, NOW(), NOW())
        ON CONFLICT (namespace, external_id) DO NOTHING;
    END LOOP;
END $$;

-- 2. ITAD UUID mapping for every Steam snapshot that carries one and has no mapping yet. An already
--    owned ('itad', uuid) mapping is never re-pointed: DO NOTHING means no cross-game merge.
DO $$
DECLARE
    r RECORD;
    target BIGINT;
BEGIN
    FOR r IN
        SELECT DISTINCT ON (sg.itad_game_id) sg.itad_game_id AS itad_id, sg.app_id, sg.name
        FROM public.steam_games sg
        WHERE sg.itad_game_id IS NOT NULL
          AND btrim(sg.itad_game_id) <> ''
          AND NOT EXISTS (
              SELECT 1 FROM public.game_external_ids e
              WHERE e.namespace = 'itad' AND e.external_id = sg.itad_game_id
          )
        ORDER BY sg.itad_game_id, sg.steam_game_id
    LOOP
        SELECT e.game_id INTO target
        FROM public.game_external_ids e
        WHERE e.namespace = 'steam' AND e.external_id = r.app_id::text
        LIMIT 1;

        IF target IS NULL THEN
            INSERT INTO public.games (title, normalized_title, created_at, updated_at)
            VALUES (
                r.name,
                btrim(regexp_replace(translate(lower(r.name), 'áàâäãåéèêëíìîïóòôöõúùûüñç', 'aaaaaaeeeeiiiiooooouuuunc'), '[^a-z0-9]+', ' ', 'g')),
                NOW(),
                NOW()
            )
            RETURNING game_id INTO target;

            INSERT INTO public.game_external_ids (game_id, namespace, external_id, created_at, updated_at)
            VALUES (target, 'steam', r.app_id::text, NOW(), NOW())
            ON CONFLICT (namespace, external_id) DO NOTHING;
        END IF;

        INSERT INTO public.game_external_ids (game_id, namespace, external_id, created_at, updated_at)
        VALUES (target, 'itad', r.itad_id, NOW(), NOW())
        ON CONFLICT (namespace, external_id) DO NOTHING;
    END LOOP;
END $$;

-- 3. Every Steam snapshot points at the canonical game of its appid.
UPDATE public.steam_games sg
SET game_id = e.game_id,
    updated_at = NOW()
FROM public.game_external_ids e
WHERE e.namespace = 'steam'
  AND e.external_id = sg.app_id::text
  AND sg.game_id IS DISTINCT FROM e.game_id;

-- 4. Every user_library row with a non-blank (store, store_game_id) ends with a game_id and its own
--    mapping. Priority: an existing source mapping, then an exact ITAD mapping, else a new canonical
--    row created from the row title. This covers Steam, Epic, GOG, Amazon, Xbox, ... alike.
DO $$
DECLARE
    r RECORD;
    target BIGINT;
BEGIN
    FOR r IN
        SELECT ul.user_library_id,
               lower(btrim(ul.store)) AS store,
               btrim(ul.store_game_id) AS store_game_id,
               ul.title,
               nullif(btrim(ul.itad_game_id), '') AS itad_id
        FROM public.user_library ul
        WHERE btrim(ul.store) <> '' AND btrim(ul.store_game_id) <> ''
        ORDER BY ul.user_library_id
    LOOP
        SELECT e.game_id INTO target
        FROM public.game_external_ids e
        WHERE e.namespace = r.store AND e.external_id = r.store_game_id
        LIMIT 1;

        IF target IS NULL AND r.itad_id IS NOT NULL THEN
            SELECT e.game_id INTO target
            FROM public.game_external_ids e
            WHERE e.namespace = 'itad' AND e.external_id = r.itad_id
            LIMIT 1;
        END IF;

        IF target IS NULL THEN
            INSERT INTO public.games (title, normalized_title, created_at, updated_at)
            VALUES (
                r.title,
                btrim(regexp_replace(translate(lower(r.title), 'áàâäãåéèêëíìîïóòôöõúùûüñç', 'aaaaaaeeeeiiiiooooouuuunc'), '[^a-z0-9]+', ' ', 'g')),
                NOW(),
                NOW()
            )
            RETURNING game_id INTO target;
        END IF;

        INSERT INTO public.game_external_ids (game_id, namespace, external_id, created_at, updated_at)
        VALUES (target, r.store, r.store_game_id, NOW(), NOW())
        ON CONFLICT (namespace, external_id) DO NOTHING;

        UPDATE public.user_library
        SET game_id = target,
            updated_at = NOW()
        WHERE user_library_id = r.user_library_id
          AND game_id IS DISTINCT FROM target;
    END LOOP;
END $$;

ANALYZE public.games;
ANALYZE public.game_external_ids;
ANALYZE public.steam_games;
ANALYZE public.user_library;
