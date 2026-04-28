-- 0025-credit-card-payments.sql
-- Create the credit_card_payments table per ADR-001. See STE-25.

-- ── credit_card_payments table ──────────────────────────────────────────────
CREATE TABLE credit_card_payments (
    id                     uuid        PRIMARY KEY,
    tenant_id              uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    credit_card_account_id uuid        NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    funding_account_id     uuid        NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    amount_minor           bigint      NOT NULL,
    currency               text        NOT NULL,
    payment_type           text        NOT NULL,
    scheduled_date         date,
    paid_at                timestamptz,
    debit_transaction_id   uuid        REFERENCES transactions(id) ON DELETE SET NULL,
    credit_transaction_id  uuid        REFERENCES transactions(id) ON DELETE SET NULL,
    created_at             timestamptz NOT NULL DEFAULT now()
);

-- Indexes
CREATE INDEX credit_card_payments_tenant_card_idx
    ON credit_card_payments (tenant_id, credit_card_account_id);

CREATE INDEX credit_card_payments_tenant_funding_idx
    ON credit_card_payments (tenant_id, funding_account_id);

-- ── RLS ─────────────────────────────────────────────────────────────────────
ALTER TABLE credit_card_payments ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON credit_card_payments
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── tenant_app privileges ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE credit_card_payments TO tenant_app;
