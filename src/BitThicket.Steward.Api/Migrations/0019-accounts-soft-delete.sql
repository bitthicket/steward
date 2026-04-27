-- 0019-accounts-soft-delete.sql
-- Add soft-delete support to the accounts table (STE-22).
-- Hard-deleting accounts breaks transaction history, so we use a
-- deleted_at timestamp and filter it out in repo queries.

ALTER TABLE accounts
    ADD COLUMN IF NOT EXISTS deleted_at timestamptz;

-- Index for efficiently filtering out soft-deleted rows
CREATE INDEX IF NOT EXISTS accounts_deleted_at_idx
    ON accounts (deleted_at)
    WHERE deleted_at IS NOT NULL;
