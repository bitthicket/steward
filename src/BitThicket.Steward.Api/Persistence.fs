namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Npgsql
open BitThicket.Steward.Api.Domain

/// Factory for obtaining Npgsql connections.  The singleton implementation
/// wraps NpgsqlDataSource.  `OpenForTenantAsync` sets `steward.tenant_id`
/// and `steward.user_id` via `set_config` (session-scoped) before returning
/// the connection so that downstream RLS policies (STE-17/18) can enforce
/// row-level isolation.
type IDbConnectionFactory =
    /// Open a connection with no tenant/user context set.
    abstract OpenAsync : unit -> Task<NpgsqlConnection>
    /// Open a connection and configure it for the given tenant context.
    abstract OpenForTenantAsync : TenantContext -> Task<NpgsqlConnection>

/// Default implementation of IDbConnectionFactory.
type DbConnectionFactory(dataSource: NpgsqlDataSource) =
    interface IDbConnectionFactory with
        member _.OpenAsync() = dataSource.OpenConnectionAsync().AsTask()
        member _.OpenForTenantAsync(ctx) =
            task {
                let! conn = dataSource.OpenConnectionAsync().AsTask()
                use cmd = conn.CreateCommand()
                // `true` = transaction-scoped.  When no transaction is active
                // this falls back to session scope, which is safe because
                // Npgsql resets session state when a pooled connection is
                // returned.  Once STE-17 wraps repo calls in transactions the
                // setting will automatically be scoped to the transaction.
                cmd.CommandText <-
                    """SELECT set_config('steward.tenant_id', $1, true);
                       SELECT set_config('steward.user_id', $2, true);"""
                cmd.Parameters.AddWithValue("$1", ctx.TenantId.ToString()) |> ignore
                cmd.Parameters.AddWithValue("$2", ctx.UserId.ToString()) |> ignore
                do! cmd.ExecuteNonQueryAsync() :> Task
                return conn
            }

/// Low-level helpers for mapping DbDataReader columns to F# values.
/// Opens a tenant-scoped connection by resolving the current TenantContext from
/// the request scope. Feature handlers should use this instead of manually
/// constructing and passing TenantContext around.
module TenantScopedConnection =
    let openAsync (ctx: Microsoft.AspNetCore.Http.HttpContext) =
        let factory = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>()
        let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
        match accessor.Context with
        | Some tc -> factory.OpenForTenantAsync(tc)
        | None -> failwith "No tenant context available for the current request"

module Sql =
    let dateTimeOffset (reader: DbDataReader) (ordinal: int) =
        DateTimeOffset(reader.GetDateTime(ordinal), TimeSpan.Zero)

    let dateOnly (reader: DbDataReader) (ordinal: int) =
        DateOnly.FromDateTime(reader.GetDateTime(ordinal))

    let nullableGuid (reader: DbDataReader) (ordinal: int) =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetGuid(ordinal))

    let nullableString (reader: DbDataReader) (ordinal: int) =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetString(ordinal))

    let nullableDecimal (reader: DbDataReader) (ordinal: int) =
        if reader.IsDBNull(ordinal) then None else Some(reader.GetDecimal(ordinal))

    let nullableDateTimeOffset (reader: DbDataReader) (ordinal: int) =
        if reader.IsDBNull(ordinal) then None else Some(dateTimeOffset reader ordinal)

/// Repository for the global (non-tenant-scoped) baseline tables.
/// Used by registration, login, and admin flows.
module RootRepository =

    // ── Tenant helpers ───────────────────────────────────────────────────────

    let getTenantById (factory: IDbConnectionFactory) (id: Guid) =
        task {
            // RLS on tenants filters by steward.tenant_id; set it to the
            // tenant being queried so the row is visible.
            let ctx = { TenantId = id; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT id, display_name, created_at, updated_at FROM tenants WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return
                if hasRow then
                    Some {
                        Id = reader.GetGuid(0)
                        DisplayName = reader.GetString(1)
                        CreatedAt = Sql.dateTimeOffset reader 2
                        UpdatedAt = Sql.dateTimeOffset reader 3
                    }
                else
                    None
        }

    let createTenant (factory: IDbConnectionFactory) (tenant: Tenant) =
        task {
            // A new tenant must be inserted with its own tenant_id set as
            // the RLS GUC, otherwise the row would be invisible to the
            // inserting connection.
            let ctx = { TenantId = tenant.Id; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO tenants (id, display_name, created_at, updated_at)
                   VALUES ($1, $2, $3, $4)"""
            cmd.Parameters.AddWithValue("$1", tenant.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", tenant.DisplayName) |> ignore
            cmd.Parameters.AddWithValue("$3", tenant.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$4", tenant.UpdatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return tenant
        }

    // ── User helpers ─────────────────────────────────────────────────────────

    let private mapUser (reader: DbDataReader) =
        {
            Id = reader.GetGuid(0)
            Email = reader.GetString(1)
            PasswordHash = reader.GetString(2)
            DisplayName = Sql.nullableString reader 3
            CreatedAt = Sql.dateTimeOffset reader 4
            UpdatedAt = Sql.dateTimeOffset reader 5
        }

    let getUserById (factory: IDbConnectionFactory) (id: Guid) =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT id, email, password_hash, display_name, created_at, updated_at FROM users WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapUser reader) else None
        }

    let getUserByEmail (factory: IDbConnectionFactory) (email: string) =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT id, email, password_hash, display_name, created_at, updated_at FROM users WHERE email = $1"
            cmd.Parameters.AddWithValue("$1", email) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapUser reader) else None
        }

    let createUser (factory: IDbConnectionFactory) (user: User) =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO users (id, email, password_hash, display_name, created_at, updated_at)
                   VALUES ($1, $2, $3, $4, $5, $6)"""
            cmd.Parameters.AddWithValue("$1", user.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", user.Email) |> ignore
            cmd.Parameters.AddWithValue("$3", user.PasswordHash) |> ignore
            match user.DisplayName with
            | Some name -> cmd.Parameters.AddWithValue("$4", name) |> ignore
            | None -> cmd.Parameters.AddWithValue("$4", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$5", user.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$6", user.UpdatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return user
        }

    // ── UserTenantMembership helpers ─────────────────────────────────────────

    let private mapMembership (reader: DbDataReader) =
        {
            UserId = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            Role = reader.GetString(2)
            CreatedAt = Sql.dateTimeOffset reader 3
        }

    /// Cross-tenant membership read used by the login flow.
    /// Bypasses RLS via the SECURITY DEFINER function get_user_memberships
    /// because the caller has not yet established a tenant context.
    let listMembershipsByUser (factory: IDbConnectionFactory) (userId: Guid) =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT user_id, tenant_id, role, created_at FROM get_user_memberships($1)"
            cmd.Parameters.AddWithValue("$1", userId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let memberships = ResizeArray<UserTenantMembership>()
            while! reader.ReadAsync() do
                memberships.Add(mapMembership reader)
            return memberships |> Seq.toList
        }

    let listMembershipsByTenant (factory: IDbConnectionFactory) (tenantId: Guid) =
        task {
            let ctx = { TenantId = tenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT user_id, tenant_id, role, created_at
                   FROM user_tenant_memberships
                   WHERE tenant_id = $1"""
            cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let memberships = ResizeArray<UserTenantMembership>()
            while! reader.ReadAsync() do
                memberships.Add(mapMembership reader)
            return memberships |> Seq.toList
        }

    let createMembership (factory: IDbConnectionFactory) (membership: UserTenantMembership) =
        task {
            let ctx = { TenantId = membership.TenantId; UserId = membership.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO user_tenant_memberships (user_id, tenant_id, role, created_at)
                   VALUES ($1, $2, $3, $4)
                   ON CONFLICT (user_id, tenant_id) DO NOTHING"""
            cmd.Parameters.AddWithValue("$1", membership.UserId) |> ignore
            cmd.Parameters.AddWithValue("$2", membership.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", membership.Role) |> ignore
            cmd.Parameters.AddWithValue("$4", membership.CreatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return membership
        }

    let deleteMembership (factory: IDbConnectionFactory) (userId: Guid) (tenantId: Guid) =
        task {
            let ctx = { TenantId = tenantId; UserId = userId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "DELETE FROM user_tenant_memberships WHERE user_id = $1 AND tenant_id = $2"
            cmd.Parameters.AddWithValue("$1", userId) |> ignore
            cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
            let! rows = cmd.ExecuteNonQueryAsync()
            return rows > 0
        }

    /// Atomically create a user, a tenant, and an owner membership linking them.
    /// Returns the created IDs on success, or an error string on duplicate email.
    let registerUserWithTenant (factory: IDbConnectionFactory) (email: string) (passwordHash: string) (displayName: string option) (tenantDisplayName: string) =
        task {
            let now = DateTimeOffset.UtcNow
            let userId = Guid.NewGuid()
            let tenantId = Guid.NewGuid()
            // Open the connection with the new tenant/user context so that
            // RLS policies on tenants and user_tenant_memberships allow the
            // inserts. users has no RLS, so the user insert is unaffected.
            let ctx = { TenantId = tenantId; UserId = userId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use tx = conn.BeginTransaction()
            try
                use userCmd = conn.CreateCommand()
                userCmd.Transaction <- tx
                userCmd.CommandText <-
                    """INSERT INTO users (id, email, password_hash, display_name, created_at, updated_at)
                       VALUES ($1, $2, $3, $4, $5, $6)"""
                userCmd.Parameters.AddWithValue("$1", userId) |> ignore
                userCmd.Parameters.AddWithValue("$2", email) |> ignore
                userCmd.Parameters.AddWithValue("$3", passwordHash) |> ignore
                match displayName with
                | Some name -> userCmd.Parameters.AddWithValue("$4", name) |> ignore
                | None -> userCmd.Parameters.AddWithValue("$4", DBNull.Value) |> ignore
                userCmd.Parameters.AddWithValue("$5", now.UtcDateTime) |> ignore
                userCmd.Parameters.AddWithValue("$6", now.UtcDateTime) |> ignore
                do! userCmd.ExecuteNonQueryAsync() :> Task

                use tenantCmd = conn.CreateCommand()
                tenantCmd.Transaction <- tx
                tenantCmd.CommandText <-
                    """INSERT INTO tenants (id, display_name, created_at, updated_at)
                       VALUES ($1, $2, $3, $4)"""
                tenantCmd.Parameters.AddWithValue("$1", tenantId) |> ignore
                tenantCmd.Parameters.AddWithValue("$2", tenantDisplayName) |> ignore
                tenantCmd.Parameters.AddWithValue("$3", now.UtcDateTime) |> ignore
                tenantCmd.Parameters.AddWithValue("$4", now.UtcDateTime) |> ignore
                do! tenantCmd.ExecuteNonQueryAsync() :> Task

                use memCmd = conn.CreateCommand()
                memCmd.Transaction <- tx
                memCmd.CommandText <-
                    """INSERT INTO user_tenant_memberships (user_id, tenant_id, role, created_at)
                       VALUES ($1, $2, $3, $4)"""
                memCmd.Parameters.AddWithValue("$1", userId) |> ignore
                memCmd.Parameters.AddWithValue("$2", tenantId) |> ignore
                memCmd.Parameters.AddWithValue("$3", "owner") |> ignore
                memCmd.Parameters.AddWithValue("$4", now.UtcDateTime) |> ignore
                do! memCmd.ExecuteNonQueryAsync() :> Task

                do! tx.CommitAsync()
                return Ok {| UserId = userId; TenantId = tenantId |}
            with
            | :? PostgresException as ex when ex.SqlState = "23505" ->
                return Error "Email already registered"
        }
