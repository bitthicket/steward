# ADR-012: Persistence layer design

## Status

Accepted

## Context

STE-16 requires a persistence layer that:
- Manages `Npgsql` connections via a singleton factory.
- Supports per-request tenant context so that future RLS policies can enforce row-level isolation.
- Provides a `RootRepository` for non-tenant operations (registration, login).
- Is tested with integration tests running against a real Postgres instance in CI.

## Decision

### No query library — hand-rolled Npgsql

We use raw `Npgsql` with `IDbConnectionFactory` rather than Dapper, Donald, or Dapper.FSharp.

**Rationale:**
- The domain is small and the query surface is narrow. Adding a query library adds a dependency, another layer of abstraction, and potential runtime issues (e.g., Dapper's dynamic mapping with F# options) for limited benefit.
- Hand-rolled `NpgsqlCommand` + `DbDataReader` mapping is straightforward in F# and gives us full control over parameter binding, async disposal, and reader mapping.
- The team prefers explicit SQL in the repository modules over expression trees or generic mapping.

### IDbConnectionFactory

```fsharp
type IDbConnectionFactory =
    /// Open a connection with no tenant/user context set.
    abstract OpenAsync : unit -> Task<NpgsqlConnection>
    /// Open a connection and configure it for the given tenant context.
    abstract OpenForTenantAsync : TenantContext -> Task<NpgsqlConnection>
```

`OpenForTenantAsync` issues `SELECT set_config('steward.tenant_id', $1, true); SELECT set_config('steward.user_id', $2, true);` with `true` = transaction-scoped before returning the connection.  When no transaction is active this falls back to session scope, which is safe because Npgsql resets session state when a pooled connection is returned.  Future RLS policies (STE-17/18) will read `current_setting('steward.tenant_id')` to filter rows.

`DbConnectionFactory` is a singleton wrapping `NpgsqlDataSource`.

### RootRepository

A dedicated module `RootRepository` for the global (non-tenant-scoped) baseline tables (`tenants`, `users`, `user_tenant_memberships`).  It uses `OpenAsync` (no tenant context).  All other repositories will use `OpenForTenantAsync` once RLS is in place.

### Integration tests with Testcontainers

Integration tests use `Testcontainers.PostgreSql` to spin up a fresh Postgres container per test session.  The container is created lazily at module level.  If Docker is unavailable (e.g., local Windows dev without Docker Desktop), tests bail out early with a no-op guard rather than failing.

### Connection string environment variable

`STEWARD_DB_CONNECTIONSTRING` is read at startup.  The container and migrations fail fast if it is missing.

## Consequences

- **Minimal dependencies:** Only `Npgsql` and `dbup-postgresql` in the API project.
- **No ORM impedance mismatch:** Revery query is explicit SQL; F# types map directly via `DbDataReader` helpers (`Sql.dateTimeOffset`, `Sql.nullableGuid`, etc.).
- **Tenant isolation is a runtime configuration, not a compile-time guarantee:** Repositories must be disciplined to call `OpenForTenantAsync` for tenant-scoped tables.  RLS policies will enforce this at the database level (STE-17/18).
- **Testcontainers dependency in tests only:** `Testcontainers.PostgreSql` is a test-only package.  Tests gracefully skip when Docker is not available.
