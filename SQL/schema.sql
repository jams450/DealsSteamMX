-- Users Table
CREATE TABLE users (
    user_id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(100) UNIQUE NOT NULL,
    password VARCHAR(255) NOT NULL,
    active BOOLEAN DEFAULT TRUE,
    admin BOOLEAN DEFAULT FALSE,
    session_version INT NOT NULL DEFAULT 1,
    failed_login_count INT NOT NULL DEFAULT 0,
    locked_until TIMESTAMPTZ,
    steam_id64 VARCHAR(20),
    wishlist_synced_at TIMESTAMPTZ,
    wishlist_state VARCHAR(16),
    min_viable_discount_percent SMALLINT NOT NULL DEFAULT 50,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100)
);

CREATE TABLE user_sessions (
    session_id UUID PRIMARY KEY,
    user_id INT NOT NULL,
    refresh_token_hash VARCHAR(128) NOT NULL UNIQUE,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    replaced_by_session_id UUID,
    ip VARCHAR(64),
    user_agent VARCHAR(512),
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE CASCADE
);

CREATE INDEX idx_user_sessions_user_id ON user_sessions(user_id);
CREATE INDEX idx_user_sessions_expires_at ON user_sessions(expires_at);

-- Store library/wishlist snapshot per user. state tells wishlist from owned library entries; the
-- unique key makes a re-import an upsert instead of a duplicate. itad_game_id links the entry back to
-- steam_games.itad_game_id when the store title can be resolved.
CREATE TABLE user_library (
    user_library_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    itad_game_id VARCHAR(36),
    store VARCHAR(32) NOT NULL,
    store_game_id VARCHAR(64) NOT NULL,
    title VARCHAR(256) NOT NULL,
    state VARCHAR(16) NOT NULL,
    is_installed BOOLEAN,
    priority INT,
    added_at TIMESTAMPTZ,
    imported_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_user_library UNIQUE (user_id, store, store_game_id, state)
);

CREATE INDEX idx_user_library_user_itad ON user_library(user_id, itad_game_id);
CREATE INDEX idx_user_library_user_title ON user_library(user_id, lower(title));

CREATE TABLE steam_games (
    steam_game_id SERIAL PRIMARY KEY,
    app_id INT NOT NULL,
    name VARCHAR(512) NOT NULL,
    type VARCHAR(32),
    image_url VARCHAR(512),
    is_free BOOLEAN NOT NULL DEFAULT FALSE,
    currency VARCHAR(3),
    initial_price_minor INT,
    current_price_minor INT,
    discount_percent INT,
    lowest_price_minor INT,
    lowest_price_at TIMESTAMPTZ,
    itad_game_id VARCHAR(36),
    offers_refreshed_at TIMESTAMPTZ,
    ggdeals_refreshed_at TIMESTAMPTZ,
    epic_refreshed_at TIMESTAMPTZ,
    bundles_refreshed_at TIMESTAMPTZ,
    region VARCHAR(2) NOT NULL,
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_steam_games_app_region UNIQUE (app_id, region)
);

CREATE TABLE steam_price_observations (
    steam_price_observation_id BIGSERIAL PRIMARY KEY,
    steam_game_id INT NOT NULL REFERENCES steam_games(steam_game_id) ON DELETE CASCADE,
    currency VARCHAR(3),
    initial_price_minor INT,
    current_price_minor INT,
    discount_percent INT,
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100)
);

CREATE INDEX idx_steam_price_observations_game_observed
    ON steam_price_observations(steam_game_id, observed_at);

-- Provider-neutral current offer snapshot per (game, region, source, offer_key). Not history:
-- append-only price history stays in steam_price_observations. MXN columns are derived;
-- the original price columns are never overwritten.
-- game_id is the canonical anchor (docs/PLAN_MULTISTORE.md §5) and region is the country the offer was
-- priced for. Both are backfilled from steam_games. steam_game_id stays NOT NULL until a provider can
-- write an offer for a game that has no Steam row.
-- history_low_all_minor / history_low_currency are provider-neutral: ITAD fills them from
-- historyLow.all, gg.deals from historicalRetail / historicalKeyshops.
CREATE TABLE game_offers (
    game_offer_id BIGSERIAL PRIMARY KEY,
    steam_game_id INT NOT NULL REFERENCES steam_games(steam_game_id) ON DELETE CASCADE,
    region VARCHAR(2) NOT NULL,
    source VARCHAR(16) NOT NULL,
    offer_key VARCHAR(128) NOT NULL,
    shop_id VARCHAR(32),
    shop_name VARCHAR(128) NOT NULL,
    classification VARCHAR(16) NOT NULL,
    original_currency VARCHAR(3) NOT NULL,
    original_regular_price_minor INT,
    original_current_price_minor INT,
    mxn_regular_price_minor INT,
    mxn_current_price_minor INT,
    history_low_all_minor INT,
    history_low_currency VARCHAR(3),
    fx_rate NUMERIC(18,8),
    fx_rate_date DATE,
    fx_source VARCHAR(32),
    pricing_type VARCHAR(16) NOT NULL,
    discount_percent INT,
    deal_url VARCHAR(1024),
    observed_at TIMESTAMPTZ NOT NULL,
    drm_names TEXT[] NOT NULL DEFAULT '{}',
    platform_names TEXT[] NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_game_offers UNIQUE (steam_game_id, source, offer_key)
);

CREATE INDEX idx_game_offers_game ON game_offers(steam_game_id);

-- idx_game_offers_game_region and uq_game_offers_canonical are created further down, after the
-- canonical anchor column is added (game_offers is declared before games). uq_game_offers is kept while
-- game_id can still be NULL (steam_games.game_id is nullable by design): it is the only thing deduping
-- those rows.

-- Canonical external bundle snapshot, keyed by (source, bundle_key) and shared by every game it was
-- seen in. Not an offer: bundles never take part in the price comparison and are never written to
-- game_offers. deal_url and page_url are the provider URLs verbatim (affiliate tag included), validated
-- HTTPS only; tiers_json is the sanitized tier list (camelCase, no item ids, no raw payload) plus each
-- item's current ITAD price, the input of the read-time tier comparison. No saving is ever stored.
CREATE TABLE external_bundles (
    external_bundle_id BIGSERIAL PRIMARY KEY,
    source VARCHAR(16) NOT NULL,
    bundle_key VARCHAR(128) NOT NULL,
    title VARCHAR(512) NOT NULL,
    shop_id VARCHAR(32),
    shop_name VARCHAR(128),
    page_url VARCHAR(1024),
    deal_url VARCHAR(1024),
    details VARCHAR(600),
    published_at TIMESTAMPTZ,
    tiers_json JSONB,
    expires_at TIMESTAMPTZ,
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_external_bundles UNIQUE (source, bundle_key)
);

-- Which queried game a bundle was seen in. source is carried on the relation so a provider refresh
-- purges only its own links; a game refresh never touches another game's relations.
CREATE TABLE external_bundle_games (
    external_bundle_game_id BIGSERIAL PRIMARY KEY,
    external_bundle_id BIGINT NOT NULL REFERENCES external_bundles(external_bundle_id) ON DELETE CASCADE,
    steam_game_id INT NOT NULL REFERENCES steam_games(steam_game_id) ON DELETE CASCADE,
    source VARCHAR(16) NOT NULL,
    observed_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_external_bundle_games UNIQUE (external_bundle_id, steam_game_id)
);

CREATE INDEX idx_external_bundle_games_game ON external_bundle_games(steam_game_id, source);

-- Daily FX rate snapshot; one row per (base, quote, rate_date). No audit fields: the row is the observation.
CREATE TABLE fx_rates (
    base VARCHAR(3) NOT NULL,
    quote VARCHAR(3) NOT NULL,
    rate NUMERIC(18,8) NOT NULL,
    rate_date DATE NOT NULL,
    source VARCHAR(32) NOT NULL,
    fetched_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (base, quote, rate_date)
);

-- Canonical game identity (docs/PLAN_CATALOG.md). games + game_external_ids are the shared identity;
-- steam_games.game_id and user_library.game_id are nullable derived links added below. No existing
-- anchor (game_offers, steam_price_observations, external_bundle_games) changes.
CREATE TABLE games (
    game_id BIGSERIAL PRIMARY KEY,
    title VARCHAR(512) NOT NULL,
    normalized_title VARCHAR(512) NOT NULL,   -- comparison/display key only, never identity
    type VARCHAR(32),
    image_url VARCHAR(512),
    is_free BOOLEAN NOT NULL DEFAULT FALSE,
    release_year INT,
    first_release_date DATE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100)
);

-- Not UNIQUE on purpose: a normalized title never asserts identity.
CREATE INDEX idx_games_normalized_title ON games(normalized_title);

-- Durable identity. namespace reuses the user_library.store vocabulary plus provider namespaces.
CREATE TABLE game_external_ids (
    game_external_id BIGSERIAL PRIMARY KEY,
    game_id BIGINT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
    namespace VARCHAR(32) NOT NULL,
    external_id VARCHAR(64) NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_game_external_ids UNIQUE (namespace, external_id)
);

CREATE INDEX idx_game_external_ids_game ON game_external_ids(game_id);

-- Nullable derived links: NULL on user_library is a valid "no canonical identity" state, so neither
-- column is ever made NOT NULL.
ALTER TABLE steam_games ADD COLUMN game_id BIGINT NULL REFERENCES games(game_id);
ALTER TABLE user_library ADD COLUMN game_id BIGINT NULL REFERENCES games(game_id);
-- game_offers is declared before games, so the canonical anchor is added here instead of inline at
-- CREATE TABLE, exactly like steam_games.game_id and user_library.game_id.
ALTER TABLE game_offers ADD COLUMN game_id BIGINT NULL REFERENCES games(game_id);

CREATE INDEX idx_steam_games_game ON steam_games(game_id);
CREATE INDEX idx_user_library_game ON user_library(game_id);

CREATE INDEX idx_game_offers_game_region ON game_offers(game_id, region);

-- The future key: one offer per (canonical game, country, source, shop). Drop uq_game_offers when
-- SELECT count(*) FROM game_offers WHERE game_id IS NULL returns 0.
CREATE UNIQUE INDEX uq_game_offers_canonical ON game_offers(game_id, region, source, offer_key);

-- Per-platform reviews (docs/PLAN_LIBRARY.md §9). Deliberately NOT foreign-keyed to user_library: that
-- row is an import artifact whose unique key includes state, so a reimport or a state change recreates
-- it. (game_id, platform) is stable. There is NO unique key: a game played twice has two reviews and
-- both survive. The score label is computed, never persisted, and there are no CHECK constraints
-- (validation lives in ReviewService), matching the rest of the schema.
CREATE TABLE game_reviews (
    game_review_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    game_id BIGINT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
    platform VARCHAR(32) NOT NULL,   -- same vocabulary as user_library.store
    started_month DATE,              -- day 1 of the month
    finished_month DATE,
    score SMALLINT,                  -- 0..100
    is_goty BOOLEAN NOT NULL DEFAULT FALSE,
    status VARCHAR(16) NOT NULL DEFAULT 'finished',  -- finished | completed | dropped (por jugar = sin reseña)
    body TEXT,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100)
);

CREATE INDEX idx_game_reviews_game ON game_reviews(game_id);
CREATE INDEX idx_game_reviews_user_game_platform ON game_reviews(user_id, game_id, platform);

-- Favoritos (SQL/migrations/2026-10-01_play_status_and_favorites.sql). Una fila presente = favorito; el
-- `game_id` de una fusion se repunta, nunca se pierde. No es una columna de game_reviews porque un
-- favorito es del juego, no de la partida: con varias reseñas no habría forma de saber cuál manda.
CREATE TABLE user_game_favorites (
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    game_id BIGINT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    PRIMARY KEY (user_id, game_id)
);

CREATE INDEX idx_user_game_favorites_game ON user_game_favorites(game_id);

-- Manual canonical merge (log). Identity is repointed, never aliased: the absorbed games row is
-- deleted after every referrer has moved. No FK on purpose: absorbed_game_id no longer exists and
-- the log must survive its own subject. Undo is deferred to v1.1; this log is the only record of
-- what moved, so it ships in v1.
CREATE TABLE IF NOT EXISTS public.game_merges (
    game_merge_id      BIGSERIAL PRIMARY KEY,
    survivor_game_id   BIGINT NOT NULL,
    absorbed_game_id   BIGINT NOT NULL,
    absorbed_snapshot  JSONB  NOT NULL,
    moved_external_ids INT NOT NULL,
    moved_steam_games  INT NOT NULL,
    moved_library_rows INT NOT NULL,
    dropped_reviews    INT NOT NULL,
    merged_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    merged_by          VARCHAR(100)
);

CREATE INDEX IF NOT EXISTS idx_game_merges_survivor ON public.game_merges(survivor_game_id);

-- Execution log of the periodic HostedServices (see SQL/migrations/2026-10-01_job_runs.sql). Event log:
-- no audit columns, the row's whole audit is started_at/finished_at. One row per cycle, whatever its
-- outcome, so a job can ask "did I already run in the last N hours?" instead of replaying a full pass on
-- every process start. trigger distinguishes 'startup' recovery passes, 'scheduled' slot runs and
-- 'manual' admin-triggered runs; details holds counters as JSONB, never secrets.
CREATE TABLE IF NOT EXISTS public.job_runs (
    job_run_id  BIGSERIAL PRIMARY KEY,
    job         VARCHAR(64) NOT NULL,
    trigger     VARCHAR(16) NOT NULL,
    status      VARCHAR(16) NOT NULL,
    started_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    finished_at TIMESTAMPTZ NULL,
    details     JSONB NULL
);

CREATE INDEX IF NOT EXISTS idx_job_runs_job_started ON public.job_runs(job, started_at DESC);
