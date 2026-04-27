-- 0012-credential-vault.sql
-- Per-tenant encrypted credential vault for OAuth tokens and provider secrets.
-- Referenced opaque ref stored in data_feed_connections.credential_ref.
-- See STE-27 and ADR-016.

CREATE TABLE credential_vault (
    id            uuid        PRIMARY KEY,
    tenant_id     uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    ref           text        NOT NULL UNIQUE,
    ciphertext    bytea       NOT NULL,
    nonce         bytea       NOT NULL,
    key_version   int         NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX credential_vault_tenant_id_idx
    ON credential_vault (tenant_id);

CREATE INDEX credential_vault_ref_idx
    ON credential_vault (ref);

-- ── RLS ─────────────────────────────────────────────────────────────────────
ALTER TABLE credential_vault ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON credential_vault
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── updated_at trigger ──────────────────────────────────────────────────────
CREATE TRIGGER credential_vault_updated_at
    BEFORE UPDATE ON credential_vault
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE credential_vault TO tenant_app;
