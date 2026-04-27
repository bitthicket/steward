-- 0011-sync-events.sql
-- Create the sync_events table per ADR-005 / ADR-011. See STE-20.

CREATE TABLE sync_events (
    id                   uuid        PRIMARY KEY,
    tenant_id            uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    connection_id        uuid        NOT NULL REFERENCES data_feed_connections(id) ON DELETE CASCADE,
    started_at           timestamptz NOT NULL,
    completed_at         timestamptz,
    status               jsonb       NOT NULL,
    transactions_added   int         NOT NULL DEFAULT 0,
    transactions_updated int         NOT NULL DEFAULT 0
);

CREATE INDEX sync_events_tenant_id_idx
    ON sync_events (tenant_id);

CREATE INDEX sync_events_connection_id_idx
    ON sync_events (connection_id);

CREATE INDEX sync_events_started_at_idx
    ON sync_events (started_at DESC);

-- ── RLS ────────────────────────────────────────────────────────────────────
ALTER TABLE sync_events ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON sync_events
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── tenant_app privileges ──────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE sync_events TO tenant_app;
