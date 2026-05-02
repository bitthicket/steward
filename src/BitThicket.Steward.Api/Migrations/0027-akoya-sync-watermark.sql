-- 0027-akoya-sync-watermark.sql
-- Add provider_metadata JSON field to data_feed_connections for storing
-- per-connection sync watermark state (Akoya: lastSyncedAt).
-- See STE-36.

-- Already added to schema via sync-coordinator migration; this migration
-- ensures the Akoya-specific metadata shape is documented and any missing
-- last_synced_at handling is formalized.

COMMENT ON COLUMN data_feed_connections.last_synced_at IS
    'Timestamp of the most recently completed successful sync. NULL means never synced. Used by the Akoya ingestion service to compute startDate for incremental transaction fetches.';

-- If provider_metadata lacks lastSyncedAt (legacy connections), ingestion
-- falls back to fetching all transactions.

-- ── SECURITY DEFINER helper for updating last_synced_at from ingestion ──────
CREATE OR REPLACE FUNCTION update_connection_last_synced_at(p_id uuid)
RETURNS void
LANGUAGE sql
SECURITY DEFINER
SET row_security = off
WITH (cost = 10)
AS $$
    UPDATE data_feed_connections
    SET last_synced_at = now()
    WHERE id = p_id;
$$;

GRANT EXECUTE ON FUNCTION update_connection_last_synced_at(uuid) TO tenant_app;
