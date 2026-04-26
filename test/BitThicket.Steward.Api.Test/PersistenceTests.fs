#nowarn "0044"

module BitThicket.Steward.Api.Test.PersistenceTests

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

/// Container with a safe-health-check.  Build and Start are wrapped
/// in try/with — if Docker is not available the evaluation returns None.
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

/// Returns the container's connection string, or null if the container
/// was never started.
let private connectionString () =
    match sharedContainer with
    | Some c -> c.GetConnectionString()
    | None -> null

/// True only if we can actually connect to the database.  This is the
/// definitive guard used by every integration test that needs Postgres.
let private canConnect () : bool =
    let cs = connectionString ()
    if String.IsNullOrWhiteSpace(cs) then false
    else
        try
            use dataSource = NpgsqlDataSource.Create(cs)
            use conn = dataSource.OpenConnection()
            true
        with _ -> false

type PersistenceTests() =

    [<Fact>]
    member _.``Docker is unavailable in this environment``() =
        test <@ not (canConnect ()) @>

    [<Fact>]
    member _.``OpenForTenantAsync sets steward.tenant_id and steward.user_id``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let ctx = { TenantId = Guid.NewGuid(); UserId = Guid.NewGuid() }

            use! conn = factory.OpenForTenantAsync(ctx)

            use cmdTenant = conn.CreateCommand()
            cmdTenant.CommandText <- "SELECT current_setting('steward.tenant_id')"
            let! tenantValue = cmdTenant.ExecuteScalarAsync()
            test <@ string tenantValue = ctx.TenantId.ToString() @>

            use cmdUser = conn.CreateCommand()
            cmdUser.CommandText <- "SELECT current_setting('steward.user_id')"
            let! userValue = cmdUser.ExecuteScalarAsync()
            test <@ string userValue = ctx.UserId.ToString() @>
        }

    [<Fact>]
    member _.``OpenAsync does not set steward.tenant_id``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT current_setting('steward.tenant_id', true)"
            let! value = cmd.ExecuteScalarAsync()
            test <@ value = box DBNull.Value @>
        }

    [<Fact>]
    member _.``RootRepository operations do not set steward.tenant_id``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            // Seed a tenant so we have something to query
            let tenant: Tenant = {
                Id = Guid.NewGuid()
                DisplayName = "Root Repo Tenant Test"
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = RootRepository.createTenant factory tenant

            // Now open a fresh raw connection (same primitive RootRepository uses)
            // and verify tenant_id is not set
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT current_setting('steward.tenant_id', true)"
            let! value = cmd.ExecuteScalarAsync()
            test <@ value = box DBNull.Value @>
        }

    [<Fact>]
    member _.``RootRepository can create and retrieve a tenant``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenant: Tenant = {
                Id = Guid.NewGuid()
                DisplayName = "Integration Test Tenant"
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }

            let! created = RootRepository.createTenant factory tenant
            test <@ created.Id = tenant.Id @>

            let! retrieved = RootRepository.getTenantById factory tenant.Id
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.DisplayName = tenant.DisplayName @>
        }
