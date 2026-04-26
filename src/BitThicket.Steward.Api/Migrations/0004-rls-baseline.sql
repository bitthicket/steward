-- 0004-rls-baseline.sql
-- Enable row-level security on tenant-scoped baseline tables, create the
-- runtime application role, and provide SECURITY DEFINER helpers for the
-- small number of cross-tenant reads the login flow requires.
-- See STE-17 and ADR-013.

-- ── Runtime application role ────────────────────────────────────────────────
-- tenant_app is the role the API connects as at runtime. It must NOT have
-- BYPASSRLS — RLS is the load-bearing isolation mechanism.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tenant_app') THEN
        CREATE ROLE tenant_app WITH LOGIN NOINHERIT;
    END IF;
END
$$;

-- Grant schema usage and table privileges on existing tables.
-- Future migrations that create new tables must grant privileges to tenant_app
-- and, for tenant-scoped tables, enable RLS and create a matching policy.
GRANT USAGE ON SCHEMA public TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE tenants TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE users TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE user_tenant_memberships TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE prices TO tenant_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE schemaversions TO tenant_app;

-- ── Enable RLS on tenant-scoped tables ──────────────────────────────────────
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_tenant_memberships ENABLE ROW LEVEL SECURITY;

-- Do NOT FORCE row level security. The migration runner (table owner) needs
-- to be able to bypass RLS for admin-time operations. tenant_app does not
-- have BYPASSRLS, so policies are enforced for the runtime role.

-- ── Tenant isolation policies ───────────────────────────────────────────────
-- Every tenant-scoped table gets a policy that filters rows by the
-- steward.tenant_id GUC set by IDbConnectionFactory.OpenForTenantAsync.

CREATE POLICY tenant_isolation ON tenants
    FOR ALL
    TO tenant_app
    USING (id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (id = current_setting('steward.tenant_id')::uuid);

CREATE POLICY tenant_isolation ON user_tenant_memberships
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

-- ── SECURITY DEFINER helpers for cross-tenant reads ─────────────────────────
-- The login flow must discover all tenants a user belongs to before a
-- specific tenant context is established. These functions bypass RLS
-- internally so tenant_app (which cannot bypass RLS) can execute them.

CREATE OR REPLACE FUNCTION get_user_memberships(p_user_id uuid)
RETURNS TABLE(user_id uuid, tenant_id uuid, role text, created_at timestamptz)
LANGUAGE sql
SECURITY DEFINER
SET row_security = off
AS $$
    SELECT user_id, tenant_id, role, created_at
    FROM user_tenant_memberships
    WHERE user_id = p_user_id;
$$;

GRANT EXECUTE ON FUNCTION get_user_memberships(uuid) TO tenant_app;

-- ── Assert BYPASSRLS is false for tenant_app ────────────────────────────────
DO $$
DECLARE
    bypass boolean;
BEGIN
    SELECT rolbypassrls INTO bypass
    FROM pg_roles
    WHERE rolname = 'tenant_app';

    IF bypass IS TRUE THEN
        RAISE EXCEPTION 'tenant_app must NOT have BYPASSRLS. RLS is the load-bearing isolation mechanism.';
    END IF;
END
$$;
