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

