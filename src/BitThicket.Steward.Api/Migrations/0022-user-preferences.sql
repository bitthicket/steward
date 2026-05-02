-- 0022-user-preferences.sql
-- Per-user per-tenant preferences, including default display currency.
-- See STE-48.

CREATE TABLE IF NOT EXISTS user_preferences (
    user_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    default_currency_code text NOT NULL DEFAULT 'USD',
    default_budgeting_style text NOT NULL DEFAULT 'flexible',
    preferred_sync_frequency interval NOT NULL DEFAULT '1 hour',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, tenant_id),
    CONSTRAINT fk_user_preferences_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_user_preferences_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT chk_currency_code_length CHECK (length(default_currency_code) = 3)
);

-- RLS: users can only see/update their own preferences within their tenant.
ALTER TABLE user_preferences ENABLE ROW LEVEL SECURITY;

CREATE POLICY user_preferences_isolation ON user_preferences
    USING (tenant_id = current_setting('steward.tenant_id', true)::uuid);

GRANT SELECT, INSERT, UPDATE, DELETE ON user_preferences TO tenant_app;
