-- 0015-feed-health-and-remediation.sql
-- Create feed_health and remediation_attempts tables per ADR-011. See STE-20.

-- ── feed_health table ─────────────────────────────────────────────────────
-- Coarse, computed projection of feed connection health. One row per connection.
CREATE TABLE feed_health (
    connection_id              uuid        PRIMARY KEY REFERENCES data_feed_connections(id) ON DELETE CASCADE,
    tenant_id                  uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    level                      text        NOT NULL DEFAULT 'unknown',
    last_success_at            timestamptz,
    last_failure_at            timestamptz,
    consecutive_failures       int         NOT NULL DEFAULT 0,
    open_remediation_attempt_id uuid,
    evaluated_at               timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX feed_health_tenant_id_idx
    ON feed_health (tenant_id);

CREATE INDEX feed_health_level_idx
    ON feed_health (level)
    WHERE level IN ('degraded', 'failing');

-- ── remediation_attempts table ────────────────────────────────────────────
CREATE TABLE remediation_attempts (
    id                  uuid        PRIMARY KEY,
    tenant_id           uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    connection_id       uuid        NOT NULL REFERENCES data_feed_connections(id) ON DELETE CASCADE,
    started_at          timestamptz NOT NULL,
    completed_at        timestamptz,
    actor_agent_id      uuid,
    actor_user_id       uuid,
    strategy            text        NOT NULL,
    outcome             jsonb,
    notes               text
);

CREATE INDEX remediation_attempts_tenant_id_idx
    ON remediation_attempts (tenant_id);

CREATE INDEX remediation_attempts_connection_id_idx
    ON remediation_attempts (connection_id);

CREATE INDEX remediation_attempts_started_at_idx
    ON remediation_attempts (started_at DESC);

-- ── RLS ────────────────────────────────────────────────────────────────────
ALTER TABLE feed_health ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON feed_health
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

ALTER TABLE remediation_attempts ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON remediation_attempts
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── tenant_app privileges ──────────────────────────────────────────────────
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE feed_health TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE remediation_attempts TO tenant_app;
