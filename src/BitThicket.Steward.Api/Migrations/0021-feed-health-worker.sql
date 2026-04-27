-- 0021-feed-health-worker.sql
-- SECURITY DEFINER helpers and indexes for the feed-health background worker.
-- See STE-30.

-- Index to make sync-event lookups per connection fast.
CREATE INDEX IF NOT EXISTS sync_events_connection_started_idx
    ON sync_events (connection_id, started_at DESC);

-- ── SECURITY DEFINER helpers for cross-tenant feed-health computation ──────
-- The feed-health worker is a BackgroundService with no tenant context.
-- It needs to read connections and sync events across all tenants.

-- Returns all data feed connections.
CREATE OR REPLACE FUNCTION get_all_data_feed_connections()
RETURNS TABLE(
    id uuid,
    tenant_id uuid,
    user_id uuid,
    provider_metadata jsonb,
    credential_ref text,
    status jsonb,
    linked_account_ids jsonb,
    created_at timestamptz,
    updated_at timestamptz
)
LANGUAGE sql
SECURITY DEFINER
SET row_security = off
AS $$
    SELECT
        dfc.id,
        dfc.tenant_id,
        dfc.user_id,
        dfc.provider_metadata,
        dfc.credential_ref,
        dfc.status,
        dfc.linked_account_ids,
        dfc.created_at,
        dfc.updated_at
    FROM data_feed_connections dfc;
$$;

GRANT EXECUTE ON FUNCTION get_all_data_feed_connections() TO tenant_app;

-- Returns sync events for a given connection, newest first.
CREATE OR REPLACE FUNCTION get_sync_events_for_connection(p_connection_id uuid)
RETURNS TABLE(
    id uuid,
    tenant_id uuid,
    connection_id uuid,
    started_at timestamptz,
    completed_at timestamptz,
    status jsonb,
    transactions_added int,
    transactions_updated int
)
LANGUAGE sql
SECURITY DEFINER
SET row_security = off
AS $$
    SELECT
        se.id,
        se.tenant_id,
        se.connection_id,
        se.started_at,
        se.completed_at,
        se.status,
        se.transactions_added,
        se.transactions_updated
    FROM sync_events se
    WHERE se.connection_id = p_connection_id
    ORDER BY se.started_at DESC;
$$;

GRANT EXECUTE ON FUNCTION get_sync_events_for_connection(uuid) TO tenant_app;

-- Returns the most recent open remediation attempt for a connection.
-- An open attempt is one with no outcome (outcome IS NULL).
CREATE OR REPLACE FUNCTION get_open_remediation_attempt(p_connection_id uuid)
RETURNS TABLE(
    id uuid,
    tenant_id uuid,
    connection_id uuid,
    started_at timestamptz,
    completed_at timestamptz,
    actor_agent_id uuid,
    actor_user_id uuid,
    strategy text,
    outcome jsonb,
    notes text
)
LANGUAGE sql
SECURITY DEFINER
SET row_security = off
AS $$
    SELECT
        ra.id,
        ra.tenant_id,
        ra.connection_id,
        ra.started_at,
        ra.completed_at,
        ra.actor_agent_id,
        ra.actor_user_id,
        ra.strategy,
        ra.outcome,
        ra.notes
    FROM remediation_attempts ra
    WHERE ra.connection_id = p_connection_id
      AND ra.outcome IS NULL
    ORDER BY ra.started_at DESC
    LIMIT 1;
$$;

GRANT EXECUTE ON FUNCTION get_open_remediation_attempt(uuid) TO tenant_app;
