-- 0017-transactions-category-occurred-idx.sql
-- Covering index for budget-vs-actual reporting queries (STE-39).
-- Supports filtering by tenant + category + occurred_at range for spend aggregation.

CREATE INDEX IF NOT EXISTS transactions_tenant_category_occurred_idx
    ON transactions (tenant_id, category_id, occurred_at);
