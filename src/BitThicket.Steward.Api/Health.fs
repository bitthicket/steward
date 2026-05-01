namespace BitThicket.Steward.Api

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Npgsql
open BitThicket.Steward.Api.Vault

type HealthCheckResult =
    | Healthy of string
    | Unhealthy of string

/// Performs readiness checks for the API.
type HealthChecker(connectionString: string, logger: ILogger) =

    /// Verify the database is reachable.
    member _.CheckDatabaseAsync() =
        task {
            try
                use dataSource = NpgsqlDataSource.Create(connectionString)
                use! conn = dataSource.OpenConnectionAsync()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT 1"
                let! result = cmd.ExecuteScalarAsync()
                if Convert.ToInt32(result) = 1 then
                    return Healthy "Database connection OK"
                else
                    return Unhealthy "Database returned unexpected result"
            with ex ->
                logger.LogError(ex, "Database health check failed")
                return Unhealthy $"Database connection failed: {ex.Message}"
        }

    /// Verify the vault encryption key can perform an encrypt/decrypt roundtrip.
    member _.CheckVaultAsync() =
        task {
            try
                let keyInfo = VaultKeyResolver.currentKey()
                let keyBytes =
                    match keyInfo with
                    | VaultKey.Current(_, bytes) -> bytes
                    | _ -> failwith "Expected current key"

                let plaintext = System.Text.Encoding.UTF8.GetBytes("vault-health-check")
                let nonce, ciphertext, _ = AesGcm256.encrypt keyBytes plaintext
                let decrypted = AesGcm256.decrypt keyBytes nonce ciphertext
                let result = System.Text.Encoding.UTF8.GetString(decrypted)

                if result = "vault-health-check" then
                    return Healthy "Vault encrypt/decrypt roundtrip OK"
                else
                    return Unhealthy "Vault roundtrip returned mismatched data"
            with ex ->
                logger.LogError(ex, "Vault health check failed")
                return Unhealthy $"Vault roundtrip failed: {ex.Message}"
        }

    /// Verify there are no pending database migrations.
    member _.CheckMigrationsAsync() =
        task {
            try
                let upgrader = Migrations.buildUpgrader connectionString
                let pending : System.Collections.Generic.IReadOnlyList<DbUp.Engine.SqlScript> = upgrader.GetScriptsToExecute()
                if pending.Count = 0 then
                    return Healthy "All migrations applied"
                else
                    return Unhealthy $"{pending.Count} pending migration(s)"
            with ex ->
                logger.LogError(ex, "Migration health check failed")
                return Unhealthy $"Migration check failed: {ex.Message}"
        }

    /// Run all checks and return an aggregated result.
    member this.CheckAllAsync() =
        task {
            let! dbResult = this.CheckDatabaseAsync()
            let! vaultResult = this.CheckVaultAsync()
            let! migrationResult = this.CheckMigrationsAsync()

            let checks = [
                "database", dbResult
                "vault", vaultResult
                "migrations", migrationResult
            ]

            let allHealthy =
                checks
                |> List.forall (fun (_, r) ->
                    match r with Healthy _ -> true | _ -> false)

            let status = if allHealthy then "healthy" else "unhealthy"
            let statusCode = if allHealthy then 200 else 503
            return statusCode, status, checks
        }
