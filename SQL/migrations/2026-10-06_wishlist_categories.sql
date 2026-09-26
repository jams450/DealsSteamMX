-- Wishlist categories. Apply after 2026-09-24_wishlist_and_steam_id.sql.
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
    wishlist_category_id BIGINT NOT NULL,
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
