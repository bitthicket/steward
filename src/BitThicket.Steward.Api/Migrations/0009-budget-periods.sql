-- 0009-budget-periods.sql
-- Budget period and per-period category allocation tables per ADR-004. See STE-20.

-- ── budget_periods table ────────────────────────────────────────────────────
CREATE TABLE budget_periods (
    id         uuid        PRIMARY KEY,
    budget_id  uuid        NOT NULL REFERENCES budgets(id) ON DELETE CASCADE,
    tenant_id  uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    start_date date        NOT NULL,
    end_date   date        NOT NULL,
    status     text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX budget_periods_budget_id_idx
    ON budget_periods (budget_id);

CREATE INDEX budget_periods_tenant_id_idx
    ON budget_periods (tenant_id);

-- ── RLS ─────────────────────────────────────────────────────────────────────
ALTER TABLE budget_periods ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON budget_periods
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── updated_at trigger ──────────────────────────────────────────────────────
CREATE TRIGGER budget_periods_updated_at
    BEFORE UPDATE ON budget_periods
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE budget_periods TO tenant_app;

-- ── budget_period_categories table ──────────────────────────────────────────
CREATE TABLE budget_period_categories (
    budget_period_id       uuid    NOT NULL REFERENCES budget_periods(id) ON DELETE CASCADE,
    category_id            uuid    NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
    allocated_minor        bigint  NOT NULL,
    opening_balance_minor  bigint  NOT NULL DEFAULT 0,
    rollover_balance_minor bigint  NOT NULL DEFAULT 0,
    currency               text    NOT NULL,
    rollover_enabled       boolean NOT NULL DEFAULT false,
    tenant_id              uuid    NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    PRIMARY KEY (budget_period_id, category_id)
);

CREATE INDEX budget_period_categories_period_id_idx
    ON budget_period_categories (budget_period_id);

CREATE INDEX budget_period_categories_tenant_id_idx
    ON budget_period_categories (tenant_id);

-- ── RLS ─────────────────────────────────────────────────────────────────────
ALTER TABLE budget_period_categories ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON budget_period_categories
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── tenant_app privileges ───────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE budget_period_categories TO tenant_app;
