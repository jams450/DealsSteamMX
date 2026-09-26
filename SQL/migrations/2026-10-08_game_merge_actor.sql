-- Persist authenticated actor identity for canonical merge audit rows.
-- No credentials or personal payloads are stored; actor_user_id is the existing user key.
ALTER TABLE public.game_merges
    ADD COLUMN IF NOT EXISTS actor_user_id INT;
