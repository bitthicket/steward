-- 0023-user-preferences.sql
-- Per-tenant user preferences including sync frequency.
-- See STE-28.

CREATE TABLE user_preferences (
    user_id                   uuid        PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    tenant_id                 uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    default_currency_code     text        NOT NULL DEFAULT 'USD',
    default_budgeting_style   text        NOT NULL DEFAULT 'Flexible',
    preferred_sync_frequency  interval    NOT NULL DEFAULT '1 hour'::interval,
    updated_at                timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX user_preferences_tenant_id_idx
    ON user_preferences (tenant_id);

-- Foreign key unique index doubles as tenant-scoped lookup
CREATE INDEX user_preferences_user_id_tenant_id_idx
    ON user_preferences (user_id, tenant_id);

-- ── RLS ────────────────────────────────────────────────────────────────────
ALTER TABLE user_preferences ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON user_preferences
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── updated_at trigger ─────────────────────────────────────────────────────
CREATE TRIGGER user_preferences_updated_at
    BEFORE UPDATE ON user_preferences
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ──────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE ON TABLE user_preferences TO tenant_app;
