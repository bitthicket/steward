module BitThicket.Steward.Api.Test.HealthTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Xunit
open Swensen.Unquote
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Vault

// ── Test helpers ─────────────────────────────────────────────────────────────

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

let private runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

let private createLogger () =
    let factory = LoggerFactory.Create(fun b -> b.AddConsole() |> ignore)
    factory.CreateLogger("Test")

// ── Tests ────────────────────────────────────────────────────────────────────

type HealthTests() =

    [<Fact>]
    member _.``/health/ready returns 200 when all subsystems healthy``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs

            // Set a temporary vault key so the vault check passes
            let originalKey = Environment.GetEnvironmentVariable("STEWARD_VAULT_KEY")
            let testKey = Convert.ToBase64String(Array.create 32 (byte 1))
            Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", testKey)

            try
                let logger = createLogger ()
                let checker = HealthChecker(cs, logger)
                let! statusCode, status, checks = checker.CheckAllAsync()

                test <@ statusCode = 200 @>
                test <@ status = "healthy" @>
                test <@ checks |> List.forall (fun (_, r) -> match r with Healthy _ -> true | _ -> false) @>
            finally
                if isNull originalKey then
                    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", null)
                else
                    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", originalKey)
        }

    [<Fact>]
    member _.``/health/ready returns 503 when database is unreachable``() =
        task {
            let logger = createLogger ()
            let checker = HealthChecker("Host=invalid;Database=test;Username=test;Password=test", logger)
            let! statusCode, status, checks = checker.CheckAllAsync()

            test <@ statusCode = 503 @>
            test <@ status = "unhealthy" @>

            let dbCheck = checks |> List.find (fun (name, _) -> name = "database")
            test <@ match snd dbCheck with Unhealthy _ -> true | _ -> false @>
        }

    [<Fact>]
    member _.``/health/ready returns 503 when vault key is missing``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs

            let originalKey = Environment.GetEnvironmentVariable("STEWARD_VAULT_KEY")
            Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", null)

            try
                let logger = createLogger ()
                let checker = HealthChecker(cs, logger)
                let! statusCode, status, checks = checker.CheckAllAsync()

                test <@ statusCode = 503 @>
                test <@ status = "unhealthy" @>

                let vaultCheck = checks |> List.find (fun (name, _) -> name = "vault")
                test <@ match snd vaultCheck with Unhealthy _ -> true | _ -> false @>
            finally
                if isNull originalKey then
                    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", null)
                else
                    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", originalKey)
        }

    [<Fact>]
    member _.``Database health check returns Healthy for valid connection``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            let logger = createLogger ()
            let checker = HealthChecker(cs, logger)
            let! result = checker.CheckDatabaseAsync()

            test <@ match result with Healthy _ -> true | _ -> false @>
        }

    [<Fact>]
    member _.``Vault health check returns Healthy with valid key``() =
        task {
            let originalKey = Environment.GetEnvironmentVariable("STEWARD_VAULT_KEY")
            let testKey = Convert.ToBase64String(Array.create 32 (byte 1))
            Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", testKey)

            try
                let logger = createLogger ()
                let checker = HealthChecker("", logger)
                let! result = checker.CheckVaultAsync()

                test <@ match result with Healthy _ -> true | _ -> false @>
            finally
                if isNull originalKey then
                    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", null)
                else
                    Environment.SetEnvironmentVariable("STEWARD_VAULT_KEY", originalKey)
        }

    [<Fact>]
    member _.``Migration health check returns Healthy when all applied``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            let logger = createLogger ()
            let checker = HealthChecker(cs, logger)
            let! result = checker.CheckMigrationsAsync()

            test <@ match result with Healthy _ -> true | _ -> false @>
        }
