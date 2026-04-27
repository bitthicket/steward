namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for the computed feed_health projection.
type IFeedHealthRepository =
    abstract GetAsync : connectionId:Guid -> Task<FeedHealth option>
    abstract ListForTenantAsync : unit -> Task<FeedHealth list>
    abstract UpsertAsync : feedHealth:FeedHealth -> Task<unit>
    abstract ListDegradedOrFailingAsync : unit -> Task<FeedHealth list>

module FeedHealthRepository =

    let private feedHealthLevelToString (level: FeedHealthLevel) : string =
        match level with
        | FeedHealthLevel.Healthy  -> "healthy"
        | FeedHealthLevel.Degraded -> "degraded"
        | FeedHealthLevel.Failing  -> "failing"
        | FeedHealthLevel.Unknown  -> "unknown"

    let private feedHealthLevelFromString (s: string) : FeedHealthLevel =
        match s.ToLowerInvariant() with
        | "healthy"  -> FeedHealthLevel.Healthy
        | "degraded" -> FeedHealthLevel.Degraded
        | "failing"  -> FeedHealthLevel.Failing
        | _          -> FeedHealthLevel.Unknown

    let private mapFeedHealth (reader: DbDataReader) : FeedHealth =
        {
            ConnectionId = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            Level = feedHealthLevelFromString (reader.GetString(2))
            LastSuccessAt = Sql.nullableDateTimeOffset reader 3
            LastFailureAt = Sql.nullableDateTimeOffset reader 4
            ConsecutiveFailures = reader.GetInt32(5)
            OpenRemediationAttemptId = Sql.nullableGuid reader 6
            EvaluatedAt = Sql.dateTimeOffset reader 7
        }

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (connectionId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT connection_id, tenant_id, level, last_success_at, last_failure_at,
                          consecutive_failures, open_remediation_attempt_id, evaluated_at
                   FROM feed_health WHERE connection_id = $1"""
            cmd.Parameters.AddWithValue("$1", connectionId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapFeedHealth reader) else None
        }

    let listForTenantAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT connection_id, tenant_id, level, last_success_at, last_failure_at,
                          consecutive_failures, open_remediation_attempt_id, evaluated_at
                   FROM feed_health
                   ORDER BY evaluated_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let results = ResizeArray<FeedHealth>()
            while! reader.ReadAsync() do
                results.Add(mapFeedHealth reader)
            return results |> Seq.toList
        }

    let upsertAsync (factory: IDbConnectionFactory) (feedHealth: FeedHealth) =
        task {
            let ctx = { TenantId = feedHealth.TenantId; UserId = Guid.Empty }
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
            cmd.Parameters.AddWithValue("$1", feedHealth.ConnectionId) |> ignore
            cmd.Parameters.AddWithValue("$2", feedHealth.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", feedHealthLevelToString feedHealth.Level) |> ignore
            match feedHealth.LastSuccessAt with
            | Some d -> cmd.Parameters.AddWithValue("$4", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$4", DBNull.Value) |> ignore
            match feedHealth.LastFailureAt with
            | Some d -> cmd.Parameters.AddWithValue("$5", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$6", feedHealth.ConsecutiveFailures) |> ignore
            match feedHealth.OpenRemediationAttemptId with
            | Some id -> cmd.Parameters.AddWithValue("$7", id) |> ignore
            | None -> cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$8", feedHealth.EvaluatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let listDegradedOrFailingAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT connection_id, tenant_id, level, last_success_at, last_failure_at,
                          consecutive_failures, open_remediation_attempt_id, evaluated_at
                   FROM feed_health
                   WHERE level IN ('degraded', 'failing')
                   ORDER BY evaluated_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let results = ResizeArray<FeedHealth>()
            while! reader.ReadAsync() do
                results.Add(mapFeedHealth reader)
            return results |> Seq.toList
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IFeedHealthRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IFeedHealthRepository with
            member _.GetAsync(connectionId) = getAsync factory (requireCtx()) connectionId
            member _.ListForTenantAsync() = listForTenantAsync factory (requireCtx())
            member _.UpsertAsync(feedHealth) = upsertAsync factory feedHealth
            member _.ListDegradedOrFailingAsync() = listDegradedOrFailingAsync factory (requireCtx())
        }
