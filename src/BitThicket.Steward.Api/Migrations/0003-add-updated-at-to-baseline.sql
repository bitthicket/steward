-- 0003-add-updated-at-to-baseline.sql
-- Align baseline tables with domain model by adding updated_at columns.

ALTER TABLE tenants
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
