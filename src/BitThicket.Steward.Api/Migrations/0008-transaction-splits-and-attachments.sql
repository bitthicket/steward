-- 0008-transaction-splits-and-attachments.sql
-- Create transaction_splits and attachments tables per ADR-008.
-- Includes sum-to-parent trigger for split integrity. See STE-20.

-- ── transaction_splits table ────────────────────────────────────────────────
CREATE TABLE transaction_splits (
    id            uuid        PRIMARY KEY,
    tenant_id     uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    transaction_id uuid       NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    amount_minor  bigint      NOT NULL,
    currency      text        NOT NULL,
    category_id   uuid        REFERENCES categories(id) ON DELETE SET NULL,
    description   text,
    memo          text,
    source        jsonb       NOT NULL DEFAULT '{"type":"manual"}'::jsonb,
    sort_order    int         NOT NULL DEFAULT 0,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX transaction_splits_tenant_id_idx
    ON transaction_splits (tenant_id);

CREATE INDEX transaction_splits_transaction_id_idx
    ON transaction_splits (transaction_id);

-- ── attachments table ─────────────────────────────────────────────────────
CREATE TABLE attachments (
    id                  uuid        PRIMARY KEY,
    tenant_id           uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    transaction_id      uuid        NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    split_id            uuid        REFERENCES transaction_splits(id) ON DELETE SET NULL,
    kind                text        NOT NULL,
    storage_ref         text        NOT NULL,
    content_hash        text        NOT NULL,
    content_type        text        NOT NULL,
    size_bytes          bigint      NOT NULL,
    uploaded_at         timestamptz NOT NULL,
    uploaded_by_user_id uuid,
    uploaded_by_agent_id uuid
);

CREATE INDEX attachments_tenant_id_idx
    ON attachments (tenant_id);

CREATE INDEX attachments_transaction_id_idx
    ON attachments (transaction_id);

-- ── Sum-to-parent trigger ─────────────────────────────────────────────────
CREATE OR REPLACE FUNCTION enforce_split_sum()
RETURNS TRIGGER AS $$
DECLARE
    parent_amount_minor bigint;
    parent_currency text;
    split_sum bigint;
BEGIN
    -- Get parent transaction amount and currency
    SELECT amount_minor, currency INTO parent_amount_minor, parent_currency
    FROM transactions WHERE id = NEW.transaction_id;

    -- Currency mismatch is an error
    IF NEW.currency <> parent_currency THEN
        RAISE EXCEPTION 'Split currency (%) does not match parent transaction currency (%)',
            NEW.currency, parent_currency;
    END IF;

    -- Compute sum of all splits for this transaction
    SELECT COALESCE(SUM(amount_minor), 0) INTO split_sum
    FROM transaction_splits
    WHERE transaction_id = NEW.transaction_id;

    IF split_sum <> parent_amount_minor THEN
        RAISE EXCEPTION 'Split amounts do not sum to parent transaction amount: expected %, got %',
            parent_amount_minor, split_sum;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER transaction_splits_sum_check
    AFTER INSERT OR UPDATE ON transaction_splits
    FOR EACH ROW
    EXECUTE FUNCTION enforce_split_sum();

-- ── RLS ───────────────────────────────────────────────────────────────────
ALTER TABLE transaction_splits ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON transaction_splits
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

ALTER TABLE attachments ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON attachments
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── updated_at triggers ───────────────────────────────────────────────────
CREATE TRIGGER transaction_splits_updated_at
    BEFORE UPDATE ON transaction_splits
    FOR EACH ROW
    EXECUTE FUNCTION set_updated_at();

-- ── tenant_app privileges ─────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE transaction_splits TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE attachments TO tenant_app;
