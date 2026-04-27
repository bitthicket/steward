-- 0016-budget-periods.sql
-- Budget period tables per STE-38. Depends on 0010-budgets.sql and 0006-categories.sql.

-- ── Add income to budgets (needed for zero-based invariant) ───────────────
ALTER TABLE budgets
    ADD COLUMN IF NOT EXISTS income_minor     bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS income_currency  text   NOT NULL DEFAULT 'USD';

-- ── budget_periods table ─────────────────────────────────────────────────
CREATE TABLE budget_periods (
    id          uuid        PRIMARY KEY,
    budget_id   uuid        NOT NULL REFERENCES budgets(id) ON DELETE CASCADE,
    tenant_id   uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    start_date  date        NOT NULL,
    end_date    date        NOT NULL,
    status      text        NOT NULL DEFAULT 'Open',
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT valid_status CHECK (status IN ('Open', 'Closed'))
);

CREATE INDEX budget_periods_budget_id_idx
    ON budget_periods (budget_id);

CREATE INDEX budget_periods_budget_id_status_idx
    ON budget_periods (budget_id, status);

CREATE UNIQUE INDEX budget_periods_one_open_per_budget_idx
    ON budget_periods (budget_id) WHERE status = 'Open';

-- ── budget_period_categories table ───────────────────────────────────────
CREATE TABLE budget_period_categories (
    budget_period_id        uuid        NOT NULL REFERENCES budget_periods(id) ON DELETE CASCADE,
    category_id             uuid        NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
    allocated_minor         bigint      NOT NULL DEFAULT 0,
    opening_balance_minor   bigint      NOT NULL DEFAULT 0,
    rollover_balance_minor  bigint      NOT NULL DEFAULT 0,
    currency                text        NOT NULL,
    rollover_enabled        boolean     NOT NULL DEFAULT false,
    PRIMARY KEY (budget_period_id, category_id)
);

CREATE INDEX budget_period_categories_period_idx
    ON budget_period_categories (budget_period_id);

-- ── RLS ──────────────────────────────────────────────────────────────────
ALTER TABLE budget_periods ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON budget_periods
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

ALTER TABLE budget_period_categories ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON budget_period_categories
    FOR ALL
    TO tenant_app
    USING (
        budget_period_id IN (
            SELECT id FROM budget_periods
            WHERE tenant_id = current_setting('steward.tenant_id')::uuid
        )
    )
    WITH CHECK (
        budget_period_id IN (
            SELECT id FROM budget_periods
            WHERE tenant_id = current_setting('steward.tenant_id')::uuid
        )
    );

-- ── updated_at trigger ───────────────────────────────────────────────────
CREATE TRIGGER budget_periods_updated_at
    BEFORE UPDATE ON budget_periods
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE budget_periods TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE budget_period_categories TO tenant_app;
