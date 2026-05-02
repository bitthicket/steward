-- 0021-transactions-soft-delete.sql
-- Add soft-delete support to the transactions table (STE-23).

ALTER TABLE transactions
    ADD COLUMN IF NOT EXISTS deleted_at timestamptz;

CREATE INDEX IF NOT EXISTS transactions_deleted_at_idx
    ON transactions (deleted_at)
    WHERE deleted_at IS NOT NULL;
