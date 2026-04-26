-- 0005-accounts.sql
-- Create the accounts table — the first domain table. Establishes the pattern
-- (RLS shape, repo signature, upsert handling) copied for all subsequent
-- domain tables. See STE-19.

-- ── Generic updated_at trigger (idempotent) ────────────────────────────────
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- ── accounts table ──────────────────────────────────────────────────────────
CREATE TABLE accounts (
    id                uuid        PRIMARY KEY,
    tenant_id         uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id           uuid        NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    name              text        NOT NULL,
    account_type      text        NOT NULL,
    currency          text        NOT NULL,
    institution_name  text,
    external_id       text,
    credit_card_info  jsonb,
    is_on_budget      boolean     NOT NULL,
    is_active         boolean     NOT NULL DEFAULT true,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now()
);

-- Indexes
CREATE INDEX accounts_tenant_id_idx
    ON accounts (tenant_id);

CREATE INDEX accounts_tenant_id_user_id_idx
    ON accounts (tenant_id, user_id);

-- ── RLS ─────────────────────────────────────────────────────────────────────
ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON accounts
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── updated_at trigger ──────────────────────────────────────────────────────
CREATE TRIGGER accounts_updated_at
    BEFORE UPDATE ON accounts
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE accounts TO tenant_app;
