-- 0018-api-keys.sql
-- API key table for programmatic and MCP authentication.
-- Each key belongs to a user+tenant pair and is scoped by role.
-- The raw key is stored as a SHA-256 hash; only the prefix (first 8 chars)
-- is stored in plaintext for display purposes. The full key is shown once
-- at creation time and never again.
-- See STE-41.

CREATE TABLE api_keys (
    id            uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id       uuid        NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    display_name  text        NOT NULL DEFAULT 'API Key',
    key_hash      text        NOT NULL,
    key_prefix    text        NOT NULL,
    role          text        NOT NULL DEFAULT 'member',
    scopes        text[]      NOT NULL DEFAULT '{}',
    expires_at    timestamptz,
    last_used_at  timestamptz,
    revoked_at    timestamptz,
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX api_keys_tenant_user_idx ON api_keys (tenant_id, user_id);
CREATE INDEX api_keys_key_prefix_idx   ON api_keys (key_prefix);

-- RLS
ALTER TABLE api_keys ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON api_keys
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- tenant_app privileges
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE api_keys TO tenant_app;
