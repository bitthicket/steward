-- Migration 0024: Reconciliations and reconciliation-transactions link table

CREATE TYPE reconciliation_status AS ENUM ('open', 'completed', 'aborted');

CREATE TABLE reconciliations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    account_id UUID NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    statement_date DATE NOT NULL,
    statement_balance_minor BIGINT NOT NULL,
    currency TEXT NOT NULL,
    status reconciliation_status NOT NULL DEFAULT 'open',
    note TEXT,
    created_by_user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ
);

CREATE TABLE reconciliation_transactions (
    reconciliation_id UUID NOT NULL REFERENCES reconciliations(id) ON DELETE CASCADE,
    transaction_id UUID NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    PRIMARY KEY (reconciliation_id, transaction_id)
);

-- Indexes
CREATE INDEX idx_reconciliations_tenant_account ON reconciliations(tenant_id, account_id);
CREATE INDEX idx_reconciliations_status ON reconciliations(status);
CREATE INDEX idx_reconciliation_transactions_reconciliation_id ON reconciliation_transactions(reconciliation_id);
CREATE INDEX idx_reconciliation_transactions_transaction_id ON reconciliation_transactions(transaction_id);

-- RLS
ALTER TABLE reconciliations ENABLE ROW LEVEL SECURITY;
ALTER TABLE reconciliation_transactions ENABLE ROW LEVEL SECURITY;

CREATE POLICY reconciliations_tenant_isolation ON reconciliations
    USING (tenant_id = current_setting('steward.tenant_id')::UUID);

CREATE POLICY reconciliation_transactions_tenant_isolation ON reconciliation_transactions
    USING (reconciliation_id IN (
        SELECT id FROM reconciliations WHERE tenant_id = current_setting('steward.tenant_id')::UUID
    ));
