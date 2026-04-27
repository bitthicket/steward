# ADR-013: Row-level security for tenant isolation

## Status

Accepted

## Context

STE-17 requires tenant isolation at the database level. The application is multi-tenant: every entity (except the global `users` table) is scoped to a tenant. We need a mechanism that:

1. Enforces isolation even if application code accidentally omits a `WHERE tenant_id = ...` clause.
2. Survives SQL injection — a malicious query cannot escape the tenant boundary.
3. Is simple enough that future developers copy the pattern correctly without deep PostgreSQL expertise.

## Decision

### RLS is the load-bearing isolation mechanism

PostgreSQL's **Row-Level Security (RLS)** is enabled on every tenant-scoped table. Each table gets a policy that compares the row's `tenant_id` to the `steward.tenant_id` GUC set on the connection by `IDbConnectionFactory.OpenForTenantAsync`.

The runtime application role (`tenant_app`) **does not** have `BYPASSRLS`. The migration runner role (often the table owner) may bypass RLS for admin-time operations.

### Per-table RLS shape

Every new tenant-scoped table must follow this exact shape in its migration:

```sql
-- 1. Create the table with a tenant_id column
CREATE TABLE accounts (
    id         uuid        PRIMARY KEY,
    tenant_id  uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    -- ... other columns
    created_at timestamptz NOT NULL DEFAULT now()
);

-- 2. Enable RLS
ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;

-- 3. Grant privileges to the runtime role
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE accounts TO tenant_app;

-- 4. Create the tenant isolation policy
CREATE POLICY tenant_isolation ON accounts
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);
```

Do **not** use `FORCE ROW LEVEL SECURITY`. The migration runner (table owner) needs to bypass RLS for seeding, backfills, and migrations. `tenant_app` cannot bypass RLS because it does not own the tables and does not have the `BYPASSRLS` attribute.

### Runtime role: `tenant_app`

`tenant_app` is created in `0004-rls-baseline.sql`:

```sql
CREATE ROLE tenant_app WITH LOGIN NOINHERIT;
```

It receives:
- `USAGE` on the `public` schema.
- `SELECT, INSERT, UPDATE, DELETE` on every table.
- No `BYPASSRLS`.

The Northflank runtime connection string uses `tenant_app`. A separate admin connection string (`STEWARD_DB_MIGRATION_CONNECTIONSTRING`) is used for DbUp migrations.

### Cross-tenant reads

Some flows (notably login) must read across tenant boundaries before a tenant context is established. Rather than weakening the RLS policy, we use `SECURITY DEFINER` functions that disable RLS internally:

```sql
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
```

These functions are owned by the migration admin role. `tenant_app` can execute them but cannot bypass RLS on the raw tables directly.

### Connection factory behavior

`DbConnectionFactory.OpenForTenantAsync` sets both `steward.tenant_id` and `steward.user_id` via `set_config(..., true)` before returning the connection. Repositories that operate on tenant-scoped tables must call `OpenForTenantAsync`.

`OpenAsync` (no context) is reserved for:
- The global `users` table (no RLS).
- Calling `SECURITY DEFINER` functions that bypass RLS internally.

### What is NOT tenant-scoped

The following tables do **not** have RLS:

| Table | Why |
|-------|-----|
| `users` | A user can belong to multiple tenants. The row is global. |
| `prices` | Public reference data. No PII, no tenant scoping. |

## Consequences

- **Defense in depth:** Even a repository bug or SQL injection cannot leak cross-tenant data because PostgreSQL filters the rows before they reach the application.
- **Minimal runtime privilege:** `tenant_app` has no schema-modification rights and cannot bypass RLS. A compromised runtime connection string is limited to reading/writing rows for the current tenant context.
- **Explicit cross-tenant paths:** Cross-tenant reads are visible in the schema (the `SECURITY DEFINER` functions) and can be audited, rather than hidden behind a broad role privilege.
- **Migration hygiene required:** Every new tenant-scoped table must get `ENABLE ROW LEVEL SECURITY`, a `GRANT`, and a policy. Missing any step silently breaks isolation for that table.

## Related decisions

- [ADR-012](012-persistence-layer.md): `IDbConnectionFactory` and `OpenForTenantAsync` design.
- [STE-6 plan](/STE/issues/STE-6#document-plan): multi-tenant principle.
