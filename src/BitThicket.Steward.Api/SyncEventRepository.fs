namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped sync events.
type ISyncEventRepository =
    abstract CreateAsync : syncEvent:SyncEvent -> Task<Guid>
    abstract GetAsync : id:Guid -> Task<SyncEvent option>
    abstract ListForConnectionAsync : connectionId:Guid -> Task<SyncEvent list>

module SyncEventRepository =

    let private jsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let private syncStatusToJsonb (status: SyncStatus) : obj =
        match status with
        | SyncStatus.Success ->
            box """{"type":"success"}"""
        | SyncStatus.PartialSuccess errors ->
            let errs = errors |> List.map (fun e -> e.Replace("\\", "\\\\").Replace("\"", "\\\""))
            let arr = String.Join(",", errs |> List.map (fun e -> $"\"{e}\""))
            box $"""{{"type":"partial_success","errors":[{arr}]}}"""
        | SyncStatus.Failed reason ->
            let safe = reason.Replace("\\", "\\\\").Replace("\"", "\\\"")
            box $"""{{"type":"failed","reason":"{safe}"}}"""

    let private syncStatusFromJsonb (reader: DbDataReader) (ordinal: int) : SyncStatus =
        if reader.IsDBNull(ordinal) then SyncStatus.Success
        else
            let json = reader.GetString(ordinal)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
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

    let private mapSyncEvent (reader: DbDataReader) : SyncEvent =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            ConnectionId = reader.GetGuid(2)
            StartedAt = Sql.dateTimeOffset reader 3
            CompletedAt = Sql.nullableDateTimeOffset reader 4
            Status = syncStatusFromJsonb reader 5
            TransactionsAdded = reader.GetInt32(6)
            TransactionsUpdated = reader.GetInt32(7)
        }

    let createAsync (factory: IDbConnectionFactory) (syncEvent: SyncEvent) =
        task {
            let ctx = { TenantId = syncEvent.TenantId; UserId = Guid.Empty }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO sync_events (
                       id, tenant_id, connection_id, started_at, completed_at,
                       status, transactions_added, transactions_updated
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)"""
            cmd.Parameters.AddWithValue("$1", syncEvent.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", syncEvent.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", syncEvent.ConnectionId) |> ignore
            cmd.Parameters.AddWithValue("$4", syncEvent.StartedAt.UtcDateTime) |> ignore
            match syncEvent.CompletedAt with
            | Some d -> cmd.Parameters.AddWithValue("$5", d.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
            let statusParam = cmd.CreateParameter()
            statusParam.ParameterName <- "$6"
            statusParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            statusParam.Value <- syncStatusToJsonb syncEvent.Status
            cmd.Parameters.Add(statusParam) |> ignore
            cmd.Parameters.AddWithValue("$7", syncEvent.TransactionsAdded) |> ignore
            cmd.Parameters.AddWithValue("$8", syncEvent.TransactionsUpdated) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return syncEvent.Id
        }

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, connection_id, started_at, completed_at,
                          status, transactions_added, transactions_updated
                   FROM sync_events WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapSyncEvent reader) else None
        }

    let listForConnectionAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (connectionId: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, connection_id, started_at, completed_at,
                          status, transactions_added, transactions_updated
                   FROM sync_events
                   WHERE connection_id = $1
                   ORDER BY started_at DESC
                   LIMIT 100"""
            cmd.Parameters.AddWithValue("$1", connectionId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let results = ResizeArray<SyncEvent>()
            while! reader.ReadAsync() do
                results.Add(mapSyncEvent reader)
            return results |> Seq.toList
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : ISyncEventRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new ISyncEventRepository with
            member _.CreateAsync(syncEvent) = createAsync factory syncEvent
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.ListForConnectionAsync(connectionId) = listForConnectionAsync factory (requireCtx()) connectionId
        }
