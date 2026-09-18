-- Per-user minimum viable discount for wishlist deal scoring. 0..95 percent, default 50.
-- Persisted so the frontend wishlist does not have to recompute the user's threshold per request.

ALTER TABLE public.users
    ADD COLUMN IF NOT EXISTS min_viable_discount_percent SMALLINT NOT NULL DEFAULT 50;

ALTER TABLE public.users
    DROP CONSTRAINT IF EXISTS chk_users_min_viable_discount_percent;

ALTER TABLE public.users
    ADD CONSTRAINT chk_users_min_viable_discount_percent
    CHECK (min_viable_discount_percent BETWEEN 0 AND 95);

ANALYZE public.users;
