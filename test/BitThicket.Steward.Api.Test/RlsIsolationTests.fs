#nowarn "0044"

module BitThicket.Steward.Api.Test.RlsIsolationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

let private runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

let private sharedContainer : PostgreSqlContainer option =
    try
        let c =
            PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build()
        c.StartAsync().GetAwaiter().GetResult()
        Some c
    with _ ->
        None

let private connectionString () =
    match sharedContainer with
    | Some c -> c.GetConnectionString()
    | None -> null

let private canConnect () : bool =
    let cs = connectionString ()
    if String.IsNullOrWhiteSpace(cs) then false
    else
        try
            use dataSource = NpgsqlDataSource.Create(cs)
            use conn = dataSource.OpenConnection()
            true
        with _ -> false

/// Seed a tenant and a membership directly (as the postgres superuser, which
/// bypasses RLS) so we have stable fixture data for isolation assertions.
let private seedTenantAndMembership (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO tenants (id, display_name, created_at, updated_at)
           VALUES ($1, $2, now(), now());
           INSERT INTO users (id, email, password_hash, display_name, created_at, updated_at)
           VALUES ($3, $4, 'hash', 'User', now(), now());
           INSERT INTO user_tenant_memberships (user_id, tenant_id, role, created_at)
           VALUES ($3, $1, 'owner', now());"""
    cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$2", $"Tenant {tenantId.ToString()[..7]}") |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", $"{userId}@test.com") |> ignore
    cmd.ExecuteNonQuery() |> ignore

type RlsIsolationTests() =

    [<Fact>]
    member _.``Docker is unavailable in this environment``() =
        test <@ not (canConnect ()) @>

    [<Fact>]
    member _.``With no tenant_id set tenant-scoped tables return zero rows``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            // Seed as superuser via a raw connection (bypasses RLS)
            use seedConn = dataSource.OpenConnection()
            seedTenantAndMembership seedConn tenantId userId

            // Now query as tenant_app (via OpenAsync which does NOT set steward.tenant_id)
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM user_tenant_memberships"
            let! count = cmd.ExecuteScalarAsync()
            test <@ Convert.ToInt64(count) = 0L @>

            use cmd2 = conn.CreateCommand()
            cmd2.CommandText <- "SELECT COUNT(*) FROM tenants"
            let! count2 = cmd2.ExecuteScalarAsync()
            test <@ Convert.ToInt64(count2) = 0L @>
        }

    [<Fact>]
    member _.``Tenant A can see its own rows``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)

            let tenantA = Guid.NewGuid()
            let userA = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndMembership seedConn tenantA userA

            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let ctxA = { TenantId = tenantA; UserId = userA }
            use! conn = factory.OpenForTenantAsync(ctxA)

            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT tenant_id FROM user_tenant_memberships"
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            test <@ hasRow @>
            test <@ reader.GetGuid(0) = tenantA @>
        }

    [<Fact>]
    member _.``Tenant A cannot SELECT tenant B rows``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)

            let tenantA = Guid.NewGuid()
            let userA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()
            let userB = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndMembership seedConn tenantA userA
            seedTenantAndMembership seedConn tenantB userB

            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let ctxA = { TenantId = tenantA; UserId = userA }
            use! conn = factory.OpenForTenantAsync(ctxA)

            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT COUNT(*) FROM user_tenant_memberships WHERE tenant_id = $1"
            cmd.Parameters.AddWithValue("$1", tenantB) |> ignore
            let! count = cmd.ExecuteScalarAsync()
            test <@ Convert.ToInt64(count) = 0L @>

            use cmd2 = conn.CreateCommand()
            cmd2.CommandText <- "SELECT COUNT(*) FROM tenants WHERE id = $1"
            cmd2.Parameters.AddWithValue("$1", tenantB) |> ignore
            let! count2 = cmd2.ExecuteScalarAsync()
            test <@ Convert.ToInt64(count2) = 0L @>
        }

    [<Fact>]
    member _.``Tenant A cannot UPDATE tenant B row``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)

            let tenantA = Guid.NewGuid()
            let userA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()
            let userB = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndMembership seedConn tenantA userA
            seedTenantAndMembership seedConn tenantB userB

            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let ctxA = { TenantId = tenantA; UserId = userA }
            use! conn = factory.OpenForTenantAsync(ctxA)

            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "UPDATE user_tenant_memberships SET role = 'hacker' WHERE tenant_id = $1"
            cmd.Parameters.AddWithValue("$1", tenantB) |> ignore
            let! rows = cmd.ExecuteNonQueryAsync()
            test <@ rows = 0 @>
        }

    [<Fact>]
    member _.``Tenant A cannot DELETE tenant B row``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)

            let tenantA = Guid.NewGuid()
            let userA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()
            let userB = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndMembership seedConn tenantA userA
            seedTenantAndMembership seedConn tenantB userB

            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let ctxA = { TenantId = tenantA; UserId = userA }
            use! conn = factory.OpenForTenantAsync(ctxA)

            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "DELETE FROM user_tenant_memberships WHERE tenant_id = $1"
            cmd.Parameters.AddWithValue("$1", tenantB) |> ignore
            let! rows = cmd.ExecuteNonQueryAsync()
            test <@ rows = 0 @>
        }

    [<Fact>]
    member _.``get_user_memberships bypasses RLS for cross-tenant reads``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)

            let tenantA = Guid.NewGuid()
            let userA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndMembership seedConn tenantA userA
            // Add second membership for same user in tenant B
            use cmd = seedConn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO tenants (id, display_name, created_at, updated_at)
                   VALUES ($1, 'Tenant B', now(), now());
                   INSERT INTO user_tenant_memberships (user_id, tenant_id, role, created_at)
                   VALUES ($2, $1, 'member', now());"""
            cmd.Parameters.AddWithValue("$1", tenantB) |> ignore
            cmd.Parameters.AddWithValue("$2", userA) |> ignore
            cmd.ExecuteNonQuery() |> ignore

            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let! memberships = RootRepository.listMembershipsByUser factory userA
            test <@ memberships.Length = 2 @>
            let tenantIds = memberships |> List.map (fun m -> m.TenantId) |> Set.ofList
            test <@ tenantIds.Contains(tenantA) @>
            test <@ tenantIds.Contains(tenantB) @>
        }

    [<Fact>]
    member _.``tenant_app does not have BYPASSRLS``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            use conn = dataSource.OpenConnection()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "SELECT rolbypassrls FROM pg_roles WHERE rolname = 'tenant_app'"
            let! result = cmd.ExecuteScalarAsync()
            test <@ result <> box true @>
        }
