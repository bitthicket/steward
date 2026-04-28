-- 0025-categories-enhance.sql
-- Add currency, rollover_enabled, updated_at, and deleted_at to categories.
-- See STE-24.

ALTER TABLE categories
    ADD COLUMN currency         text        NOT NULL DEFAULT 'USD',
    ADD COLUMN rollover_enabled boolean     NOT NULL DEFAULT false,
    ADD COLUMN updated_at       timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN deleted_at       timestamptz;

-- Index for soft-delete filtering
CREATE INDEX categories_deleted_at_idx
    ON categories (deleted_at) WHERE deleted_at IS NULL;

-- updated_at trigger
CREATE TRIGGER categories_updated_at
    BEFORE UPDATE ON categories
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();
