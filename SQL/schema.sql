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

-- Provider-neutral current offer snapshot per (game, source, offer_key). Not history:
-- append-only price history stays in steam_price_observations. MXN columns are derived;
-- the original price columns are never overwritten.
-- history_low_all_minor / history_low_currency are provider-neutral: ITAD fills them from
-- historyLow.all, gg.deals from historicalRetail / historicalKeyshops.
CREATE TABLE game_offers (
    game_offer_id BIGSERIAL PRIMARY KEY,
    steam_game_id INT NOT NULL REFERENCES steam_games(steam_game_id) ON DELETE CASCADE,
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

