namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open BitThicket.Steward.Api.Domain

module FeedHealthService =

    /// Default sync frequency used when no per-user preference is available.
    /// The user_preferences table (STE-28) will eventually supply this.
    let defaultSyncFrequency = TimeSpan.FromHours(1.0)

    /// Compute the FeedHealthLevel from consecutive failures and last success age.
    let computeLevel (consecutiveFailures: int) (lastSuccessAt: DateTimeOffset option) (now: DateTimeOffset) : FeedHealthLevel =
        if consecutiveFailures > 3 then
            FeedHealthLevel.Failing
        elif lastSuccessAt |> Option.exists (fun ts -> now - ts > TimeSpan.FromTicks(defaultSyncFrequency.Ticks * 4L)) then
            FeedHealthLevel.Failing
        elif consecutiveFailures >= 1 && consecutiveFailures <= 3 then
            FeedHealthLevel.Degraded
        elif consecutiveFailures = 0 && lastSuccessAt |> Option.exists (fun ts -> now - ts <= TimeSpan.FromTicks(defaultSyncFrequency.Ticks * 2L)) then
            FeedHealthLevel.Healthy
        else
            // consecutiveFailures = 0 but last success is stale (between 2x and 4x)
            FeedHealthLevel.Degraded

    /// Analyse sync events for a connection and produce a FeedHealth record.
    let evaluateConnectionHealth
        (connectionId: Guid)
        (tenantId: Guid)
        (syncEvents: SyncEvent list)
        (openAttemptId: Guid option)
        (now: DateTimeOffset) : FeedHealth =

        if syncEvents |> List.isEmpty then
            {
                ConnectionId = connectionId
                TenantId = tenantId
                Level = FeedHealthLevel.Unknown
                LastSuccessAt = None
                LastFailureAt = None
                ConsecutiveFailures = 0
                OpenRemediationAttemptId = openAttemptId
                EvaluatedAt = now
            }
        else
            let sorted = syncEvents |> List.sortByDescending (fun se -> se.StartedAt)

            let lastSuccessAt =
                sorted
                |> List.tryFind (fun se -> se.Status = SyncStatus.Success)
                |> Option.map (fun se -> se.StartedAt)

            let lastFailureAt =
                sorted
                |> List.tryFind (fun se -> se.Status <> SyncStatus.Success)
                |> Option.map (fun se -> se.StartedAt)

            let consecutiveFailures =
                sorted
                |> List.takeWhile (fun se -> se.Status <> SyncStatus.Success)
                |> List.length

            let level = computeLevel consecutiveFailures lastSuccessAt now

            {
                ConnectionId = connectionId
                TenantId = tenantId
                Level = level
                LastSuccessAt = lastSuccessAt
                LastFailureAt = lastFailureAt
                ConsecutiveFailures = consecutiveFailures
                OpenRemediationAttemptId = openAttemptId
                EvaluatedAt = now
            }

/// Background service that periodically recomputes feed_health for all
/// connections.  Ticks every 5 minutes by default.
type FeedHealthWorker(
    factory: IDbConnectionFactory,
    logger: ILogger<FeedHealthWorker>) =
    inherit BackgroundService()

    let mapConnection (reader: DbDataReader) : Guid * Guid =
        reader.GetGuid(0), reader.GetGuid(1) // id, tenant_id

    let mapSyncEvent (reader: DbDataReader) : SyncEvent =
        let statusJson = reader.GetString(5)
        use doc = JsonDocument.Parse(statusJson)
        let root = doc.RootElement
        let status =
            match root.GetProperty("type").GetString() with
            | "success" -> SyncStatus.Success
            | "partial_success" ->
                let errors =
                    root.GetProperty("errors").EnumerateArray()
                    |> Seq.map (fun el -> el.GetString())
                    |> Seq.toList
                SyncStatus.PartialSuccess errors
            | "failed" ->
                SyncStatus.Failed(root.GetProperty("reason").GetString())
            | _ -> SyncStatus.Success

        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            ConnectionId = reader.GetGuid(2)
            StartedAt = DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)
            CompletedAt =
                if reader.IsDBNull(4) then None
                else Some(DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero))
            Status = status
            TransactionsAdded = reader.GetInt32(6)
            TransactionsUpdated = reader.GetInt32(7)
        }

    let getAllConnections () =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT id, tenant_id FROM get_all_data_feed_connections()"
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let results = ResizeArray<Guid * Guid>()
            while! reader.ReadAsync() do
                results.Add(mapConnection reader)
            return results |> Seq.toList
        }

    let getSyncEvents (connectionId: Guid) =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT id, tenant_id, connection_id, started_at, completed_at, status, transactions_added, transactions_updated FROM get_sync_events_for_connection($1)"
            cmd.Parameters.AddWithValue("$1", connectionId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let results = ResizeArray<SyncEvent>()
            while! reader.ReadAsync() do
                results.Add(mapSyncEvent reader)
            return results |> Seq.toList
        }

    let getOpenAttempt (connectionId: Guid) =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT id FROM get_open_remediation_attempt($1)"
            cmd.Parameters.AddWithValue("$1", connectionId) |> ignore
            let! result = cmd.ExecuteScalarAsync()
            return
                if isNull result then None
                else Some(result :?> Guid)
        }

    let upsertHealth (health: FeedHealth) =
        task {
            let ctx = { TenantId = health.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO feed_health (
                       connection_id, tenant_id, level, last_success_at, last_failure_at,
                       consecutive_failures, open_remediation_attempt_id, evaluated_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                   ON CONFLICT (connection_id) DO UPDATE SET
                       level = EXCLUDED.level,
                       last_success_at = EXCLUDED.last_success_at,
                       last_failure_at = EXCLUDED.last_failure_at,
                       consecutive_failures = EXCLUDED.consecutive_failures,
                       open_remediation_attempt_id = EXCLUDED.open_remediation_attempt_id,
                       evaluated_at = EXCLUDED.evaluated_at"""
            cmd.Parameters.AddWithValue("$1", health.ConnectionId) |> ignore
            cmd.Parameters.AddWithValue("$2", health.TenantId) |> ignore
            let levelStr =
                match health.Level with
                | FeedHealthLevel.Healthy  -> "healthy"
                | FeedHealthLevel.Degraded -> "degraded"
                | FeedHealthLevel.Failing  -> "failing"
                | FeedHealthLevel.Unknown  -> "unknown"
            cmd.Parameters.AddWithValue("$3", levelStr) |> ignore
            match health.LastSuccessAt with
            | Some d -> cmd.Parameters.AddWithValue("$4", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$4", DBNull.Value) |> ignore
            match health.LastFailureAt with
            | Some d -> cmd.Parameters.AddWithValue("$5", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$6", health.ConsecutiveFailures) |> ignore
            match health.OpenRemediationAttemptId with
            | Some id -> cmd.Parameters.AddWithValue("$7", id) |> ignore
            | None -> cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$8", health.EvaluatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let tick () =
        task {
            let now = DateTimeOffset.UtcNow
            let! connections = getAllConnections ()
            logger.LogInformation("FeedHealthWorker evaluating {Count} connections", connections.Length)

            for (connId, tenantId) in connections do
                try
                    let! syncEvents = getSyncEvents connId
                    let! openAttemptId = getOpenAttempt connId
                    let health = FeedHealthService.evaluateConnectionHealth connId tenantId syncEvents openAttemptId now
                    do! upsertHealth health
                with ex ->
                    logger.LogError(ex, "Failed to evaluate health for connection {ConnectionId}", connId)
        }

    override _.ExecuteAsync(ct: CancellationToken) : Task =
        task {
            logger.LogInformation("FeedHealthWorker started — evaluating every 5 minutes")
            while not ct.IsCancellationRequested do
                try
                    do! tick ()
                with ex ->
                    logger.LogError(ex, "FeedHealthWorker tick failed")
                try
                    do! Task.Delay(TimeSpan.FromMinutes(5.0), ct)
                with :? OperationCanceledException ->
                    ()
        } :> Task
