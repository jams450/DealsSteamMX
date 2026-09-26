-- Versioned pre-write detail for manual canonical merges.
-- Apply before enabling rollback/audit consumers. Contains no secrets.
CREATE TABLE IF NOT EXISTS public.game_merge_details (
    game_merge_detail_id BIGSERIAL PRIMARY KEY,
    game_merge_id BIGINT NOT NULL REFERENCES public.game_merges(game_merge_id) ON DELETE CASCADE,
    entity_type VARCHAR(32) NOT NULL,
    entity_id BIGINT,
    row_snapshot JSONB NOT NULL,
    action VARCHAR(32) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_game_merge_details_merge ON public.game_merge_details(game_merge_id, game_merge_detail_id);

-- Rollback procedure: restore from row_snapshot only after stopping writers and taking a backup.
-- The application must validate merge id, replay details in reverse order, and refuse if current rows
-- diverge. No automatic rollback is exposed by this migration.
