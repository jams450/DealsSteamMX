-- Durable outbox for the global ntfy deal alerts (T3, spec S2-S4).
--
-- One row per dedup identity: policy version + game + source + offer key + MXN price. The producer
-- (NtfyDealsJob phase A) inserts rows for fresh persisted game_offers snapshots that clear at least one of
-- the three OR rules; the dispatcher (phase B) claims due rows, revalidates them against the current
-- snapshot and delivers them. The unique index is what makes the producer idempotent: re-running it on an
-- unchanged deal inserts nothing, so a daily job never repeats the same offer endlessly.
--
-- status: pending | sent | failed | stale
--   pending - inserted, waiting for its first or next attempt
--   sent    - ntfy answered a success status; sent_at is set
--   failed  - a permanent rejection, or the attempt budget was exhausted
--   stale   - the live offer changed price/disappeared before delivery; retired, never sent
-- last_error keeps only the outcome name or the HTTP status, never the topic, the base URL, the auth token
-- or an exception body.
--
-- No unique index on (steam_game_id, source, offer_key, price_minor, trigger_version): dedup_key already
-- composes all of them and is what the producer writes and the conflict target is. The index below is the
-- one the dispatcher scans, restricted to the due predicate.

CREATE TABLE IF NOT EXISTS public.deal_alert_events (
    deal_alert_event_id BIGSERIAL PRIMARY KEY,
    dedup_key           VARCHAR(320) NOT NULL,
    steam_game_id       INT NULL,
    game_id             BIGINT NULL,
    source              VARCHAR(16) NOT NULL,
    offer_key           VARCHAR(128) NOT NULL,
    price_minor         INT NOT NULL,
    trigger_version     INT NOT NULL,
    reasons             VARCHAR(64) NOT NULL,
    score               NUMERIC(4,1) NULL,
    title               VARCHAR(512) NOT NULL,
    message             VARCHAR(1024) NOT NULL,
    click_url           VARCHAR(1024) NULL,
    status              VARCHAR(16) NOT NULL DEFAULT 'pending',
    attempts            INT NOT NULL DEFAULT 0,
    next_attempt_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error          VARCHAR(256) NULL,
    sent_at             TIMESTAMPTZ NULL,
    created_at          TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by          VARCHAR(100),
    updated_by          VARCHAR(100),
    CONSTRAINT uq_deal_alert_events_dedup UNIQUE (dedup_key)
);

-- Dispatcher scan: only pending rows whose retry deadline has passed, oldest first.
CREATE INDEX IF NOT EXISTS idx_deal_alert_events_due
    ON public.deal_alert_events(status, next_attempt_at);

ANALYZE public.deal_alert_events;
