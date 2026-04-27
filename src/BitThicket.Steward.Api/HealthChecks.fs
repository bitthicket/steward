module BitThicket.Steward.Api.HealthChecks

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Npgsql

/// Health check that verifies the PostgreSQL data source can open a connection.
type NpgsqlHealthCheck(dataSource: NpgsqlDataSource) =
    interface IHealthCheck with
        member _.CheckHealthAsync(_, cancellationToken) =
            task {
                try
                    use! conn = dataSource.OpenConnectionAsync(cancellationToken)
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <- "SELECT 1"
                    let! _ = cmd.ExecuteScalarAsync(cancellationToken)
                    return HealthCheckResult.Healthy("PostgreSQL is reachable")
                with ex ->
                    return HealthCheckResult.Unhealthy("PostgreSQL unreachable", ex)
            }

/// Registers health checks with the service collection.
let register (services: Microsoft.Extensions.DependencyInjection.IServiceCollection) (dataSource: NpgsqlDataSource) =
    services
        .AddHealthChecks()
        .AddCheck("self", (fun () -> HealthCheckResult.Healthy()), tags = [||])
        .AddCheck("postgresql", NpgsqlHealthCheck(dataSource), tags = [||])
    |> ignore
