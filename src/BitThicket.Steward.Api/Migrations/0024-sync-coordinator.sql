-- 0024-sync-coordinator.sql
-- Add sync-coordinator fields to data_feed_connections and provide
-- SECURITY DEFINER helpers for cross-tenant sync scheduling.
-- See STE-28.

-- Per-connection sync cadence.  Defaults to 1 hour; UI can override.
-- Bounded by the service layer to [15 minutes, 24 hours].
ALTER TABLE data_feed_connections
    ADD COLUMN IF NOT EXISTS preferred_sync_frequency interval NOT NULL DEFAULT '1 hour'::interval;

-- Timestamp of the most recently completed sync for this connection.
-- Updated by the sync coordinator after a successful sync event is
-- recorded.  NULL means "never synced" (eligible immediately).
ALTER TABLE data_feed_connections
    ADD COLUMN IF NOT EXISTS last_synced_at timestamptz;

-- Index to make the sync-coordinator scan fast.
CREATE INDEX IF NOT EXISTS data_feed_connections_status_active_idx
    ON data_feed_connections ((status->>'Case'))
    WHERE (status->>'Case') = 'Active';

-- ── SECURITY DEFINER helpers for the sync coordinator ───────────────────────
-- The sync coordinator is a BackgroundService with no tenant context.
-- It needs to see all connections across all tenants and compute which
-- ones are due for a sync based on preferred_sync_frequency.

-- Returns the most recent successful sync event per connection.
-- Bypasses RLS so the coordinator can read sync_events cross-tenant.
CREATE OR REPLACE FUNCTION get_last_successful_sync(p_connection_id uuid)
RETURNS TABLE(started_at timestamptz, completed_at timestamptz)
LANGUAGE sql
SECURITY DEFINER
SET row_security = off
AS $$
    SELECT started_at, completed_at
    FROM sync_events
    WHERE connection_id = p_connection_id
      AND status->>'type' = 'success'
    ORDER BY started_at DESC
    LIMIT 1;
$$;

GRANT EXECUTE ON FUNCTION get_last_successful_sync(uuid) TO tenant_app;

-- Returns all data_feed_connections that are currently Active and whose
-- last_synced_at is older than their preferred_sync_frequency (or NULL).
-- The frequency is clamped to [15 minutes, 24 hours] in SQL.
CREATE OR REPLACE FUNCTION get_connections_due_for_sync()
RETURNS TABLE(
    id uuid,
    tenant_id uuid,
    user_id uuid,
    provider_metadata jsonb,
    credential_ref text,
    status jsonb,
    linked_account_ids jsonb,
    preferred_sync_frequency interval,
    last_synced_at timestamptz,
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
        dfc.preferred_sync_frequency,
        dfc.last_synced_at,
        dfc.created_at,
        dfc.updated_at
    FROM data_feed_connections dfc
    WHERE (dfc.status->>'Case') = 'Active'
      AND (
          dfc.last_synced_at IS NULL
          OR dfc.last_synced_at + LEAST(GREATEST(dfc.preferred_sync_frequency, '15 minutes'::interval), '24 hours'::interval)
             <= now()
      );
$$;

GRANT EXECUTE ON FUNCTION get_connections_due_for_sync() TO tenant_app;

-- ── SECURITY DEFINER helper for on-demand sync validation ───────────────────
-- Returns a single data_feed_connection if it belongs to the given tenant.
-- Used by the public on-demand trigger endpoint.
CREATE OR REPLACE FUNCTION get_data_feed_connection_for_tenant(p_id uuid, p_tenant_id uuid)
RETURNS TABLE(
    id uuid,
    tenant_id uuid,
    user_id uuid,
    provider_metadata jsonb,
    credential_ref text,
    status jsonb,
    linked_account_ids jsonb,
    preferred_sync_frequency interval,
    last_synced_at timestamptz,
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
        dfc.preferred_sync_frequency,
        dfc.last_synced_at,
        dfc.created_at,
        dfc.updated_at
    FROM data_feed_connections dfc
    WHERE dfc.id = p_id AND dfc.tenant_id = p_tenant_id;
$$;

GRANT EXECUTE ON FUNCTION get_data_feed_connection_for_tenant(uuid, uuid) TO tenant_app;
