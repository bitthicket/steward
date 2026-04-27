-- 0010-ingestion-api-support.sql
-- Indexes and constraints to support the internal ingestion API.
-- See STE-26.

-- Lookup account by external_id during ingestion mapping
CREATE INDEX IF NOT EXISTS accounts_external_id_idx
    ON accounts (external_id)
    WHERE external_id IS NOT NULL;

-- Enforce idempotency for feed-sourced transactions within an account.
-- Allows upsert by (tenant, account, external_id) without ambiguity.
CREATE UNIQUE INDEX IF NOT EXISTS transactions_tenant_account_external_idx
    ON transactions (tenant_id, account_id, external_id)
    WHERE external_id IS NOT NULL;
