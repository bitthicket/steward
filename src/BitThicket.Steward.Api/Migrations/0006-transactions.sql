-- 0006-transactions.sql
-- Create the transactions table per ADR-001 and ADR-003. See STE-20.

-- ── transactions table ──────────────────────────────────────────────────────
CREATE TABLE transactions (
    id                   uuid        PRIMARY KEY,
    tenant_id            uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    account_id           uuid        NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    occurred_at          timestamptz NOT NULL,
    posted_at            timestamptz,
    amount_minor         bigint      NOT NULL,
    currency             text        NOT NULL,
    description          text        NOT NULL,
    merchant             text,
    memo                 text,
    category_id          uuid        REFERENCES categories(id) ON DELETE SET NULL,
    source               jsonb       NOT NULL,
    external_id          text,
    matched_transaction_id uuid      REFERENCES transactions(id) ON DELETE SET NULL,
    transfer_account_id  uuid        REFERENCES accounts(id) ON DELETE SET NULL,
    status               text        NOT NULL,
    match_confidence     numeric,
    sync_event_id        uuid,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now()
);

-- Indexes
CREATE INDEX transactions_tenant_account_occurred_idx
    ON transactions (tenant_id, account_id, occurred_at);

CREATE INDEX transactions_tenant_status_idx
    ON transactions (tenant_id, status);

CREATE INDEX transactions_external_id_idx
    ON transactions (external_id)
    WHERE external_id IS NOT NULL;

-- ── RLS ─────────────────────────────────────────────────────────────────────
ALTER TABLE transactions ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON transactions
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── updated_at trigger ──────────────────────────────────────────────────────
CREATE TRIGGER transactions_updated_at
    BEFORE UPDATE ON transactions
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE transactions TO tenant_app;
