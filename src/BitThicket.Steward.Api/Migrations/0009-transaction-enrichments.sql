-- 0009-transaction-enrichments.sql
-- Create the transaction_enrichments table (append-only). See ADR-008, STE-20.

CREATE TABLE transaction_enrichments (
    id                      uuid        PRIMARY KEY,
    tenant_id               uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    transaction_id          uuid        NOT NULL REFERENCES transactions(id) ON DELETE CASCADE,
    source_key              text        NOT NULL,
    status                  text        NOT NULL,
    result_payload          jsonb,
    produced_split_ids      jsonb       NOT NULL DEFAULT '[]'::jsonb,
    produced_attachment_ids jsonb       NOT NULL DEFAULT '[]'::jsonb,
    requested_at            timestamptz NOT NULL,
    completed_at            timestamptz,
    requested_by_user_id    uuid,
    requested_by_agent_id   uuid
);

CREATE INDEX transaction_enrichments_tenant_id_idx
    ON transaction_enrichments (tenant_id);

CREATE INDEX transaction_enrichments_transaction_id_idx
    ON transaction_enrichments (transaction_id);

CREATE INDEX transaction_enrichments_status_idx
    ON transaction_enrichments (status)
    WHERE status = 'pending';

-- ── RLS ────────────────────────────────────────────────────────────────────
ALTER TABLE transaction_enrichments ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON transaction_enrichments
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── tenant_app privileges ──────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE transaction_enrichments TO tenant_app;
