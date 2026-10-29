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
-- acquired_store / acquisition_type / acquisition_date are the wishlist → library acquisition metadata
-- (odd/tasks/wishlist-library-acquisition.md). They are nullable as a group: NULL means "not registered",
-- which is every imported row, so the import semantics are untouched. acquired_store is NOT the store of
-- the row: the row lives in the library of the chosen platform (store), and this says where the copy came
-- from. A subscription acquisition writes state = 'subscription'; the other four kinds write 'owned'.
-- acquired_store_type / acquisition_provider (S6/L14) are optional facts of the same acquisition: whether
-- that shop is the official storefront or a keyshop, and where the offer was discovered. Both are nullable
-- and are never inferred from a price, so an unclassified acquisition keeps NULL instead of a guess.
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
    acquired_store VARCHAR(32),
    acquisition_type VARCHAR(16),
    acquired_store_type VARCHAR(16),
    acquisition_provider VARCHAR(16),
    acquisition_date DATE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_user_library UNIQUE (user_id, store, store_game_id, state),
    -- The vocabulary is closed: an unknown value would be a row nobody can classify later.
    CONSTRAINT ck_user_library_acquisition_type CHECK (
        acquisition_type IS NULL
        OR acquisition_type IN ('purchase', 'gift', 'free', 'bundle', 'subscription')),
    -- Written together or not at all: a type with no store (or a date with no type) is a partial write.
    CONSTRAINT ck_user_library_acquisition_metadata CHECK (
        (acquisition_type IS NULL AND acquired_store IS NULL AND acquisition_date IS NULL)
        OR (acquisition_type IS NOT NULL AND acquired_store IS NOT NULL)),
    -- The closed vocabularies of the two optional source facts, mirroring WishlistAcquiredStoreTypes and
    -- WishlistAcquisitionProviders in C#. An unlisted value would be a row nobody can classify later.
    CONSTRAINT ck_user_library_acquired_store_type CHECK (
        acquired_store_type IS NULL
        OR acquired_store_type IN ('official', 'keyshop', 'unknown')),
    CONSTRAINT ck_user_library_acquisition_provider CHECK (
        acquisition_provider IS NULL
        OR acquisition_provider IN ('itad', 'ggdeals', 'direct', 'other')),
    -- Both describe the acquisition's shop, so they cannot exist without it. Separate from
    -- ck_user_library_acquisition_metadata on purpose: that constraint is already applied in some databases
    -- and the source facts arrived in a later dated migration.
    CONSTRAINT ck_user_library_acquisition_source CHECK (
        (acquired_store_type IS NULL AND acquisition_provider IS NULL)
        OR (acquisition_type IS NOT NULL AND acquired_store IS NOT NULL))
);

CREATE INDEX idx_user_library_user_itad ON user_library(user_id, itad_game_id);
CREATE INDEX idx_user_library_user_title ON user_library(user_id, lower(title));

CREATE TABLE wishlist_categories (
    wishlist_category_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    name VARCHAR(80) NOT NULL,
    normalized_name VARCHAR(80) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_wishlist_categories_user_name UNIQUE (user_id, normalized_name),
    CONSTRAINT uq_wishlist_categories_user_id UNIQUE (user_id, wishlist_category_id),
    CONSTRAINT ck_wishlist_categories_name_nonblank CHECK (length(btrim(name)) > 0)
);
CREATE INDEX idx_wishlist_categories_user ON wishlist_categories(user_id);

ALTER TABLE user_library
    ADD CONSTRAINT uq_user_library_user_library_id UNIQUE (user_id, user_library_id);

CREATE TABLE wishlist_category_items (
    wishlist_category_id BIGINT NOT NULL REFERENCES wishlist_categories(wishlist_category_id) ON DELETE CASCADE,
    user_id INT NOT NULL,
    user_library_id BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    PRIMARY KEY (wishlist_category_id, user_library_id),
    CONSTRAINT fk_wishlist_category_items_category_user FOREIGN KEY (user_id, wishlist_category_id)
        REFERENCES wishlist_categories(user_id, wishlist_category_id) ON DELETE CASCADE,
    CONSTRAINT fk_wishlist_category_items_library_user FOREIGN KEY (user_id, user_library_id)
        REFERENCES user_library(user_id, user_library_id) ON DELETE CASCADE
);
CREATE INDEX idx_wishlist_category_items_user_category ON wishlist_category_items(user_id, wishlist_category_id, user_library_id);
CREATE INDEX idx_wishlist_category_items_user_library ON wishlist_category_items(user_id, user_library_id, wishlist_category_id);

-- Local wishlist removals (odd/tasks/wishlist-library-acquisition.md S3). One row per suppressed
-- (user, store, store_game_id); the row IS the suppression, so clearing it is a delete and there is no
-- boolean to keep in sync. The wishlist sync reads this table before writing a row, so a game the person
-- acquired and dropped does not come back on the next Steam sync. Deliberately not a user_library.state:
-- the sync deletes rows Steam no longer returns and would recreate a "dismissed" state on every run.
-- No game_id on purpose: the store id is the identity (a merge cannot strand it) and nothing here takes
-- part in canonical identity.
CREATE TABLE wishlist_exclusions (
    wishlist_exclusion_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    store VARCHAR(32) NOT NULL,
    store_game_id VARCHAR(64) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_wishlist_exclusions UNIQUE (user_id, store, store_game_id),
    CONSTRAINT ck_wishlist_exclusions_store_nonblank CHECK (length(btrim(store)) > 0),
    CONSTRAINT ck_wishlist_exclusions_store_game_id_nonblank CHECK (length(btrim(store_game_id)) > 0)
);
-- No separate index on user_id: the unique constraint's index already leads with it, so every per-user
-- lookup is served by it and a second index would only add write cost.

CREATE TABLE steam_games (
    steam_game_id SERIAL PRIMARY KEY,
    app_id INT NOT NULL,
    name VARCHAR(512) NOT NULL,
    type VARCHAR(32),
    image_url VARCHAR(512),
    -- NULL: legacy/unknown; empty array: Steam snapshot fetched without publisher names.
    publishers TEXT[],
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
    microsoft_refreshed_at TIMESTAMPTZ,
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
-- priced for. Both are backfilled from steam_games. steam_game_id is nullable because a store can sell a
-- game Steam does not (microsoft, Fase 2): such an offer carries game_id and no steam_game_id. uq_game_offers
-- still dedupes the Steam-anchored rows, and uq_game_offers_canonical the rest.
-- history_low_all_minor / history_low_currency are provider-neutral: ITAD fills them from
-- historyLow.all, gg.deals from historicalRetail / historicalKeyshops.
CREATE TABLE game_offers (
    game_offer_id BIGSERIAL PRIMARY KEY,
    steam_game_id INT NULL REFERENCES steam_games(steam_game_id) ON DELETE CASCADE,
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

-- Annual review tier placements are per user, year, and reviewed play. The identity is the review, not
-- the game: a game played twice in the same year — even twice on the same platform — ranks twice, and each
-- card saves independently (docs/2026-10-20_REVIEW_TIERS.md, specs S3-S6). `game_id` stays as the merge
-- anchor: GameMergeService repoints it together with the review, and SQL/checks/game_tiers_reviews.sql
-- asserts both rows agree. Unranked reviewed plays have no row.
-- Migración manual para una base existente: SQL/migrations/2026-10-20_user_game_tiers_per_review.sql.
CREATE TABLE user_game_tiers (
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    year INT NOT NULL,
    game_review_id BIGINT NOT NULL REFERENCES game_reviews(game_review_id) ON DELETE CASCADE,
    game_id BIGINT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
    tier VARCHAR(1) NOT NULL,
    position INT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    PRIMARY KEY (user_id, year, game_review_id)
);

CREATE INDEX idx_user_game_tiers_order ON user_game_tiers(user_id, year, tier, position);

-- El FK a games se recorre en cada fusión y el de game_reviews en cada borrado de reseña: sin estos
-- índices ambos son un scan completo de la tabla.
CREATE INDEX idx_user_game_tiers_game ON user_game_tiers(game_id);
CREATE INDEX idx_user_game_tiers_review ON user_game_tiers(game_review_id);

-- Bundle acquisition (odd/tasks/bundle-expiry-acquisition.md S3-S6). Una fila por compra reportada por el
-- usuario: es a la vez el registro de adquisición y la clave de idempotencia, así que un segundo envío del
-- mismo bundle contesta desde esta fila en vez de volver a importar. Guarda su propio snapshot del
-- proveedor (snapshot_json/snapshot_hash) y los datos del tier comprado, de modo que la membresía comprada
-- sigue siendo legible aunque el refresh upstream reescriba external_bundles.tiers_json. Sin FK a
-- external_bundles a propósito: la compra es evidencia y sobrevive a una purga del proveedor.
-- platform es la plataforma del tier; el override por juego vive en bundle_purchase_games.platform.
CREATE TABLE bundle_purchases (
    bundle_purchase_id BIGSERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    source VARCHAR(16) NOT NULL,
    bundle_key VARCHAR(128) NOT NULL,
    title VARCHAR(512) NOT NULL,
    shop_id VARCHAR(32),
    shop_name VARCHAR(128),
    page_url VARCHAR(1024),
    deal_url VARCHAR(1024),
    published_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ,
    observed_at TIMESTAMPTZ NOT NULL,
    tier_key VARCHAR(64) NOT NULL,
    tier_index INT NOT NULL,
    tier_price_minor INT,
    tier_currency VARCHAR(3),
    tier_addon BOOLEAN NOT NULL DEFAULT FALSE,
    tier_items_complete BOOLEAN NOT NULL DEFAULT FALSE,
    tier_selection_count INT,
    selection_mode VARCHAR(8) NOT NULL,
    selected_count INT NOT NULL,
    platform VARCHAR(32) NOT NULL,
    acquired_at DATE,
    acquired_store VARCHAR(32),
    acquired_store_type VARCHAR(16),
    acquisition_provider VARCHAR(16),
    snapshot_hash VARCHAR(64) NOT NULL,
    snapshot_json JSONB NOT NULL,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    -- Una compra por usuario y bundle del proveedor: el replay no puede importar dos veces.
    CONSTRAINT uq_bundle_purchases UNIQUE (user_id, source, bundle_key),
    CONSTRAINT ck_bundle_purchases_selection_mode CHECK (selection_mode IN ('fixed', 'choice')),
    -- Un tier fijo no tiene conteo de selección; uno de elección lo exige. Cualquier otra combinación
    -- describiría una selección que nadie validó.
    CONSTRAINT ck_bundle_purchases_selection_count CHECK (
        (selection_mode = 'fixed' AND tier_selection_count IS NULL)
        OR (selection_mode = 'choice' AND tier_selection_count IS NOT NULL)),
    CONSTRAINT ck_bundle_purchases_selected_count CHECK (selected_count > 0),
    CONSTRAINT ck_bundle_purchases_tier_key_nonblank CHECK (length(btrim(tier_key)) > 0),
    CONSTRAINT ck_bundle_purchases_snapshot_hash_nonblank CHECK (length(btrim(snapshot_hash)) > 0),
    CONSTRAINT ck_bundle_purchases_platform_nonblank CHECK (length(btrim(platform)) > 0),
    -- Vocabularios cerrados, espejo de WishlistAcquiredStoreTypes / WishlistAcquisitionProviders en C#.
    CONSTRAINT ck_bundle_purchases_acquired_store_type CHECK (
        acquired_store_type IS NULL
        OR acquired_store_type IN ('official', 'keyshop', 'unknown')),
    CONSTRAINT ck_bundle_purchases_acquisition_provider CHECK (
        acquisition_provider IS NULL
        OR acquisition_provider IN ('itad', 'ggdeals', 'direct', 'other')),
    -- Los dos hechos de origen describen la tienda de adquisición: no existen sin ella.
    CONSTRAINT ck_bundle_purchases_acquisition_source CHECK (
        (acquired_store_type IS NULL AND acquisition_provider IS NULL)
        OR acquired_store IS NOT NULL)
);
-- Sin índice aparte por user_id: uq_bundle_purchases ya empieza por user_id y sirve tanto la lectura del
-- inventario como la búsqueda de la compra que oculta un bundle del catálogo pendiente.

-- Membresía comprada (S5): una fila por ítem seleccionado del tier, con el juego canónico resuelto y lo que
-- hizo el import. Una fila 'duplicate' NO se omite: "ya lo tenías en esta plataforma" es un hecho del
-- bundle y es lo que alimenta el inventario de duplicados (uno por juego comprado). canonical_source dice
-- cómo se decidió la identidad: el UUID de ITAD (namespace 'itad') o un vínculo canónico manual explícito.
-- game_id es NO ACTION: una fusión lo repunta (GameMergeService) y nunca borra la evidencia por cascada.
CREATE TABLE bundle_purchase_games (
    bundle_purchase_game_id BIGSERIAL PRIMARY KEY,
    bundle_purchase_id BIGINT NOT NULL REFERENCES bundle_purchases(bundle_purchase_id) ON DELETE CASCADE,
    item_key VARCHAR(128) NOT NULL,
    title VARCHAR(256) NOT NULL,
    item_type VARCHAR(32),
    itad_game_id VARCHAR(36),
    game_id BIGINT NOT NULL REFERENCES games(game_id),
    canonical_source VARCHAR(8) NOT NULL,
    platform VARCHAR(32) NOT NULL,
    outcome VARCHAR(16) NOT NULL,
    user_library_id BIGINT REFERENCES user_library(user_library_id) ON DELETE SET NULL,
    wishlist_removed BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    created_by VARCHAR(100),
    updated_by VARCHAR(100),
    CONSTRAINT uq_bundle_purchase_games UNIQUE (bundle_purchase_id, item_key),
    CONSTRAINT ck_bundle_purchase_games_canonical_source CHECK (canonical_source IN ('itad', 'manual')),
    CONSTRAINT ck_bundle_purchase_games_outcome CHECK (outcome IN ('imported', 'duplicate')),
    CONSTRAINT ck_bundle_purchase_games_platform_nonblank CHECK (length(btrim(platform)) > 0),
    CONSTRAINT ck_bundle_purchase_games_item_key_nonblank CHECK (length(btrim(item_key)) > 0)
);

CREATE INDEX idx_bundle_purchase_games_game ON bundle_purchase_games(game_id);

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
    merged_by          VARCHAR(100),
    actor_user_id      INT
);

CREATE INDEX IF NOT EXISTS idx_game_merges_survivor ON public.game_merges(survivor_game_id);

-- Versioned pre-write detail for rollback/audit. The merge service writes one row per affected or
-- deduplicated entity before destructive changes; snapshots contain data only, never secrets.
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


-- Global opt-in ntfy alert outbox.
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


CREATE TABLE IF NOT EXISTS public.global_deals (
    global_deal_id                  BIGSERIAL PRIMARY KEY,
    source                          VARCHAR(32)  NOT NULL,
    deal_key                        VARCHAR(128) NOT NULL,
    itad_game_id                    VARCHAR(36)  NOT NULL,
    game_title                      VARCHAR(512) NOT NULL,
    game_slug                       VARCHAR(256) NULL,
    game_type                       VARCHAR(32)  NULL,
    game_mature                     BOOLEAN      NOT NULL DEFAULT FALSE,
    shop_id                         VARCHAR(32)  NOT NULL,
    shop_name                       VARCHAR(128) NOT NULL,
    is_official                     BOOLEAN      NOT NULL DEFAULT FALSE,
    original_currency               VARCHAR(3)   NOT NULL,
    original_current_price_minor    INT          NOT NULL,
    original_regular_price_minor    INT          NULL,
    discount_percent                INT          NULL,
    history_low_all_minor           INT          NULL,
    history_low_currency            VARCHAR(3)   NULL,
    mxn_current_price_minor         INT          NULL,
    fx_rate                         NUMERIC(18,8) NULL,
    fx_rate_date                    DATE         NULL,
    fx_source                       VARCHAR(32)  NULL,
    pricing_type                    VARCHAR(16)  NOT NULL,
    deal_url                        VARCHAR(1024) NULL,
    expires_at                      TIMESTAMPTZ  NULL,
    observed_at                     TIMESTAMPTZ  NOT NULL,
    created_at                      TIMESTAMPTZ  DEFAULT CURRENT_TIMESTAMP,
    updated_at                      TIMESTAMPTZ  DEFAULT CURRENT_TIMESTAMP,
    created_by                      VARCHAR(100),
    updated_by                      VARCHAR(100),
    CONSTRAINT uq_global_deals UNIQUE (source, deal_key)
);

-- Wishlist exclusion matches on the exact ITAD game id.
CREATE INDEX IF NOT EXISTS idx_global_deals_itad_game
    ON public.global_deals(itad_game_id);

-- Feed scan: good, price-eligible deals ordered by discount, newest first.
CREATE INDEX IF NOT EXISTS idx_global_deals_feed
    ON public.global_deals(pricing_type, discount_percent DESC, observed_at DESC);

ANALYZE public.global_deals;

-- Bundle expiry reminder outbox (2026-10-22_bundle_alert_events.sql).
CREATE TABLE IF NOT EXISTS public.bundle_alert_events (
    bundle_alert_event_id BIGSERIAL PRIMARY KEY,
    dedup_key             VARCHAR(320) NOT NULL,
    user_id               INT          NOT NULL REFERENCES public.users(user_id) ON DELETE CASCADE,
    source                VARCHAR(16)  NOT NULL,
    source_family         VARCHAR(16)  NOT NULL,
    bundle_key            VARCHAR(128) NOT NULL,
    expires_at            TIMESTAMPTZ  NOT NULL,
    trigger_version       INT          NOT NULL,
    title                 VARCHAR(512) NOT NULL,
    message               VARCHAR(1024) NOT NULL,
    click_url             VARCHAR(1024) NULL,
    status                VARCHAR(16)  NOT NULL DEFAULT 'pending',
    attempts              INT          NOT NULL DEFAULT 0,
    next_attempt_at       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_error            VARCHAR(256) NULL,
    sent_at               TIMESTAMPTZ  NULL,
    created_at            TIMESTAMPTZ  DEFAULT CURRENT_TIMESTAMP,
    updated_at            TIMESTAMPTZ  DEFAULT CURRENT_TIMESTAMP,
    created_by            VARCHAR(100),
    updated_by            VARCHAR(100),
    CONSTRAINT uq_bundle_alert_events_dedup UNIQUE (dedup_key),
    -- El vocabulario del ciclo de vida. 'suppressed' tiene su propio valor justamente para que una compra no se
    -- lea como fallo ni como bundle movido en la auditoría.
    CONSTRAINT ck_bundle_alert_events_status
        CHECK (status IN ('pending', 'sent', 'failed', 'stale', 'suppressed')),
    -- Un aviso sin fecha no existe: la política solo encola fechas futuras dentro de la ventana roja. El NOT NULL
    -- de expires_at es el que lo impide; esta consulta 2 de SQL/checks/bundle_alert_events.sql lo comprueba.
    CONSTRAINT ck_bundle_alert_events_attempts CHECK (attempts >= 0),
    -- El click es un enlace público HTTPS o nada. Un esquema raro no llega ni a la base.
    CONSTRAINT ck_bundle_alert_events_click_url
        CHECK (click_url IS NULL OR click_url LIKE 'https://%')
);

-- Barrido del despachador: solo filas pendientes cuyo plazo de reintento ya pasó, las más viejas primero.
CREATE INDEX IF NOT EXISTS idx_bundle_alert_events_due
    ON public.bundle_alert_events(status, next_attempt_at);

-- Supresión y auditoría por operador: "¿qué avisos tiene esta cuenta para este bundle?".
CREATE INDEX IF NOT EXISTS idx_bundle_alert_events_user_bundle
    ON public.bundle_alert_events(user_id, bundle_key);

ANALYZE public.bundle_alert_events;

COMMENT ON TABLE public.bundle_alert_events IS
    'Outbox de recordatorios rojos de bundle (spec S2). Una fila por operador + familia de fuente + bundle_key + expires_at.';
COMMENT ON COLUMN public.bundle_alert_events.expires_at IS
    'Snapshot del vencimiento por el que se encoló el aviso; se revalida contra external_bundles antes de enviar.';
COMMENT ON COLUMN public.bundle_alert_events.status IS
    'pending | sent | failed | stale | suppressed';
