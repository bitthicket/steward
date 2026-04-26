-- 0013-data-feed-connections.sql
-- Data feed connections for external account aggregation (Plaid, MX, etc.).
-- See STE-33.

CREATE TABLE data_feed_connections (
    id                uuid        PRIMARY KEY,
    tenant_id         uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id           uuid        NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    provider          text        NOT NULL,
    provider_metadata jsonb       NOT NULL,
    credential_ref    text        NOT NULL,
    status            jsonb       NOT NULL,
    linked_account_ids jsonb      NOT NULL DEFAULT '[]'::jsonb,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX data_feed_connections_tenant_id_idx
    ON data_feed_connections (tenant_id);

CREATE INDEX data_feed_connections_tenant_id_user_id_idx
    ON data_feed_connections (tenant_id, user_id);

CREATE INDEX data_feed_connections_item_id_idx
    ON data_feed_connections ((provider_metadata->>'itemId'));

-- ── RLS ─────────────────────────────────────────────────────────────────────
ALTER TABLE data_feed_connections ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON data_feed_connections
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── updated_at trigger ──────────────────────────────────────────────────────
CREATE TRIGGER data_feed_connections_updated_at
    BEFORE UPDATE ON data_feed_connections
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE data_feed_connections TO tenant_app;
