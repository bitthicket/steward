-- 0010-budgets.sql
-- Create budgets and budget_categories tables per ADR-004. See STE-20.

-- ── budgets table ─────────────────────────────────────────────────────────
CREATE TABLE budgets (
    id          uuid        PRIMARY KEY,
    tenant_id   uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    user_id     uuid        NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    name        text        NOT NULL,
    style       text        NOT NULL,
    period      jsonb       NOT NULL,
    currency    text        NOT NULL,
    is_active   boolean     NOT NULL DEFAULT true,
    starts_on   date        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX budgets_tenant_id_idx
    ON budgets (tenant_id);

CREATE INDEX budgets_tenant_id_user_id_idx
    ON budgets (tenant_id, user_id);

-- ── budget_categories table ───────────────────────────────────────────────
CREATE TABLE budget_categories (
    id                  uuid        PRIMARY KEY,
    tenant_id           uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    budget_id           uuid        NOT NULL REFERENCES budgets(id) ON DELETE CASCADE,
    category_id         uuid        NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
    allocated_minor     bigint      NOT NULL,
    currency            text        NOT NULL,
    rollover_enabled    boolean     NOT NULL DEFAULT false,
    rollover_balance_minor bigint   NOT NULL DEFAULT 0,
    rollover_currency   text        NOT NULL,
    UNIQUE (budget_id, category_id)
);

CREATE INDEX budget_categories_budget_id_idx
    ON budget_categories (budget_id);

CREATE INDEX budget_categories_category_id_idx
    ON budget_categories (category_id);

-- ── RLS ────────────────────────────────────────────────────────────────────
ALTER TABLE budgets ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON budgets
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

ALTER TABLE budget_categories ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON budget_categories
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── updated_at trigger ─────────────────────────────────────────────────────
CREATE TRIGGER budgets_updated_at
    BEFORE UPDATE ON budgets
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ──────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE budgets TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE budget_categories TO tenant_app;
