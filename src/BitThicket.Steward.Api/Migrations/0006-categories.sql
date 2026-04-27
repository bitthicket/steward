-- 0006-categories.sql
-- Create the categories table. See STE-20.

-- ── categories table ────────────────────────────────────────────────────────
CREATE TABLE categories (
    id          uuid        PRIMARY KEY,
    tenant_id   uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id     uuid        NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    name        text        NOT NULL,
    parent_id   uuid        REFERENCES categories(id) ON DELETE SET NULL,
    is_system   boolean     NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- Indexes
CREATE INDEX categories_tenant_id_idx
    ON categories (tenant_id);

CREATE INDEX categories_tenant_id_user_id_idx
    ON categories (tenant_id, user_id);

-- ── RLS ─────────────────────────────────────────────────────────────────────
ALTER TABLE categories ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON categories
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── tenant_app privileges ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE categories TO tenant_app;
