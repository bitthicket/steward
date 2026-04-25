namespace BitThicket.Steward.Api

open System
open System.Reflection
open DbUp
open DbUp.Engine
open DbUp.Helpers

module Migrations =

    /// Resolve the DB connection string. Production uses
    /// STEWARD_DB_CONNECTIONSTRING; absence is fatal — DbUp must run on
    /// startup and a missing connection string means misconfiguration.
    let getConnectionString () : string =
        match Environment.GetEnvironmentVariable("STEWARD_DB_CONNECTIONSTRING") with
        | null | "" ->
            raise (InvalidOperationException(
                "STEWARD_DB_CONNECTIONSTRING is not set. The Steward API requires a Postgres connection string at startup."))
        | v -> v

    /// Build the DbUp upgrader. Migrations are embedded resources from this
    /// assembly under the `Migrations` namespace; the journal lives in the
    /// default schema's SchemaVersions table.
    let buildUpgrader (connectionString: string) : UpgradeEngine =
        EnsureDatabase.For.PostgresqlDatabase(connectionString)

        DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                fun name -> name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .LogToConsole()
            .Build()

    /// Apply all pending migrations. Throws when a script fails so the host
    /// process exits non-zero and Northflank surfaces the failed deploy.
    let apply (connectionString: string) : unit =
        let upgrader = buildUpgrader connectionString
        let result = upgrader.PerformUpgrade()
        if not result.Successful then
            raise (InvalidOperationException(
                $"Database migration failed: {result.Error.Message}", result.Error))
