namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for remediation_attempts.
/// Append-only: CREATE is always allowed; UPDATE is restricted to outcome and notes only.
type IRemediationAttemptRepository =
    abstract CreateAsync : attempt:RemediationAttempt -> Task<Guid>
    abstract GetAsync : id:Guid -> Task<RemediationAttempt option>
    abstract ListForConnectionAsync : connectionId:Guid -> Task<RemediationAttempt list>
    abstract UpdateOutcomeAsync : id:Guid -> outcome:RemediationOutcome -> notes:string option -> Task<unit>

module RemediationAttemptRepository =

    let private outcomeToJsonb (outcome: RemediationOutcome) : obj =
        match outcome with
        | RemediationOutcome.Resolved ->
            box """{"type":"resolved"}"""
        | RemediationOutcome.StillFailing reason ->
            let safe = reason.Replace("\\", "\\\\").Replace("\"", "\\\"")
            box $"""{{"type":"stillFailing","reason":"{safe}"}}"""
        | RemediationOutcome.NeedsHumanInput prompt ->
            let safe = prompt.Replace("\\", "\\\\").Replace("\"", "\\\"")
            box $"""{{"type":"needsHumanInput","prompt":"{safe}"}}"""

    let private outcomeFromJsonb (reader: DbDataReader) (ordinal: int) : RemediationOutcome option =
        if reader.IsDBNull(ordinal) then None
        else
            let json = reader.GetString(ordinal)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            match root.GetProperty("type").GetString() with
            | "resolved" -> Some RemediationOutcome.Resolved
            | "stillFailing" ->
                Some (RemediationOutcome.StillFailing(root.GetProperty("reason").GetString()))
            | "needsHumanInput" ->
                Some (RemediationOutcome.NeedsHumanInput(root.GetProperty("prompt").GetString()))
            | _ -> None

    let private mapAttempt (reader: DbDataReader) : RemediationAttempt =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            ConnectionId = reader.GetGuid(2)
            StartedAt = Sql.dateTimeOffset reader 3
            CompletedAt = Sql.nullableDateTimeOffset reader 4
            ActorAgentId = Sql.nullableGuid reader 5
            ActorUserId = Sql.nullableGuid reader 6
            Strategy = reader.GetString(7)
            Outcome = outcomeFromJsonb reader 8
            Notes = Sql.nullableString reader 9
        }

    let createAsync (factory: IDbConnectionFactory) (attempt: RemediationAttempt) =
        task {
            let ctx = { TenantId = attempt.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO remediation_attempts (
                       id, tenant_id, connection_id, started_at, completed_at,
                       actor_agent_id, actor_user_id, strategy, outcome, notes
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)"""
            cmd.Parameters.AddWithValue("$1", attempt.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", attempt.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", attempt.ConnectionId) |> ignore
            cmd.Parameters.AddWithValue("$4", attempt.StartedAt.UtcDateTime) |> ignore
            match attempt.CompletedAt with
            | Some d -> cmd.Parameters.AddWithValue("$5", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            match attempt.ActorAgentId with
            | Some id -> cmd.Parameters.AddWithValue("$6", id) |> ignore
            | None -> cmd.Parameters.AddWithValue("$6", DBNull.Value) |> ignore
            match attempt.ActorUserId with
            | Some id -> cmd.Parameters.AddWithValue("$7", id) |> ignore
            | None -> cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$8", attempt.Strategy) |> ignore
            match attempt.Outcome with
            | Some o ->
                let param = cmd.CreateParameter()
                param.ParameterName <- "$9"
                param.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
                param.Value <- outcomeToJsonb o
                cmd.Parameters.Add(param) |> ignore
            | None -> cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
            match attempt.Notes with
            | Some n -> cmd.Parameters.AddWithValue("$10", n) |> ignore
            | None -> cmd.Parameters.AddWithValue("$10", DBNull.Value) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return attempt.Id
        }

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, connection_id, started_at, completed_at,
                          actor_agent_id, actor_user_id, strategy, outcome, notes
                   FROM remediation_attempts WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapAttempt reader) else None
        }

    let listForConnectionAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (connectionId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, connection_id, started_at, completed_at,
                          actor_agent_id, actor_user_id, strategy, outcome, notes
                   FROM remediation_attempts
                   WHERE connection_id = $1
                   ORDER BY started_at DESC"""
            cmd.Parameters.AddWithValue("$1", connectionId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let results = ResizeArray<RemediationAttempt>()
            while! reader.ReadAsync() do
                results.Add(mapAttempt reader)
            return results |> Seq.toList
        }

    let updateOutcomeAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) (outcome: RemediationOutcome) (notes: string option) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE remediation_attempts SET
                       outcome = $1,
                       notes = $2,
                       completed_at = $3
                   WHERE id = $4"""
            let outcomeParam = cmd.CreateParameter()
            outcomeParam.ParameterName <- "$1"
            outcomeParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            outcomeParam.Value <- outcomeToJsonb outcome
            cmd.Parameters.Add(outcomeParam) |> ignore
            match notes with
            | Some n -> cmd.Parameters.AddWithValue("$2", n) |> ignore
            | None -> cmd.Parameters.AddWithValue("$2", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$3", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$4", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IRemediationAttemptRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IRemediationAttemptRepository with
            member _.CreateAsync(attempt) = createAsync factory attempt
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.ListForConnectionAsync(connectionId) = listForConnectionAsync factory (requireCtx()) connectionId
            member _.UpdateOutcomeAsync id outcome notes = updateOutcomeAsync factory (requireCtx()) id outcome notes
        }
