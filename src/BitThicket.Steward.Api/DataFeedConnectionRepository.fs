namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped data feed connections.
type IDataFeedConnectionRepository =
    abstract GetAsync : id:Guid -> Task<DataFeedConnection option>
    abstract GetByItemIdAsync : itemId:string -> Task<DataFeedConnection option>
    abstract ListAsync : unit -> Task<DataFeedConnection list>
    abstract CreateAsync : connection:DataFeedConnection -> Task<Guid>
    abstract UpdateAsync : connection:DataFeedConnection -> Task<unit>

module DataFeedConnectionRepository =

    let private jsonOptions =
        let opts = JsonSerializerOptions()
        opts.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.NamedFields))
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let private providerMetadataToJsonb (metadata: ProviderMetadata) : obj =
        box (JsonSerializer.Serialize(metadata, jsonOptions))

    let private providerMetadataFromJsonb (reader: DbDataReader) (ordinal: int) : ProviderMetadata =
        let json = reader.GetString(ordinal)
        JsonSerializer.Deserialize<ProviderMetadata>(json, jsonOptions)

    let private connectionStatusToJsonb (status: ConnectionStatus) : obj =
        box (JsonSerializer.Serialize(status, jsonOptions))

    let private connectionStatusFromJsonb (reader: DbDataReader) (ordinal: int) : ConnectionStatus =
        let json = reader.GetString(ordinal)
        JsonSerializer.Deserialize<ConnectionStatus>(json, jsonOptions)

    let private linkedAccountIdsToJsonb (ids: Guid list) : obj =
        box (JsonSerializer.Serialize(ids, jsonOptions))

    let private linkedAccountIdsFromJsonb (reader: DbDataReader) (ordinal: int) : Guid list =
        let json = reader.GetString(ordinal)
        JsonSerializer.Deserialize<Guid list>(json, jsonOptions)

    let private mapConnection (reader: DbDataReader) : DataFeedConnection =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            UserId = reader.GetGuid(2)
            Metadata = providerMetadataFromJsonb reader 3
            CredentialRef = reader.GetString(4)
            Status = connectionStatusFromJsonb reader 5
            LinkedAccountIds = linkedAccountIdsFromJsonb reader 6
            CreatedAt = Sql.dateTimeOffset reader 7
            UpdatedAt = Sql.dateTimeOffset reader 8
            LastSyncedAt =
                if reader.IsDBNull(9) then None
                else Some(Sql.dateTimeOffset reader 9)
        }

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, provider_metadata, credential_ref, status,
                          linked_account_ids, created_at, updated_at, last_synced_at
                   FROM data_feed_connections WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapConnection reader) else None
        }

    /// Global lookup by Plaid item_id. Bypasses RLS via SECURITY DEFINER function.
    let listAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, provider_metadata, credential_ref, status,
                          linked_account_ids, created_at, updated_at, last_synced_at
                   FROM data_feed_connections
                   ORDER BY created_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let results = ResizeArray<DataFeedConnection>()
            while! reader.ReadAsync() do
                results.Add(mapConnection reader)
            return results |> Seq.toList
        }

    let getByItemIdAsync (factory: IDbConnectionFactory) (itemId: string) =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, provider_metadata, credential_ref, status,
                          linked_account_ids, created_at, updated_at, last_synced_at
                   FROM get_data_feed_connection_by_item_id($1)"""
            cmd.Parameters.AddWithValue("$1", itemId) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapConnection reader) else None
        }

    let createAsync (factory: IDbConnectionFactory) (connection: DataFeedConnection) =
        task {
            let ctx = { TenantId = connection.TenantId; UserId = connection.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO data_feed_connections (
                       id, tenant_id, user_id, provider_metadata, credential_ref, status,
                       linked_account_ids, created_at, updated_at, last_synced_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)"""
            cmd.Parameters.AddWithValue("$1", connection.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", connection.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", connection.UserId) |> ignore
            let metaParam = cmd.CreateParameter()
            metaParam.ParameterName <- "$4"
            metaParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            metaParam.Value <- providerMetadataToJsonb connection.Metadata
            cmd.Parameters.Add(metaParam) |> ignore
            cmd.Parameters.AddWithValue("$5", connection.CredentialRef) |> ignore
            let statusParam = cmd.CreateParameter()
            statusParam.ParameterName <- "$6"
            statusParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            statusParam.Value <- connectionStatusToJsonb connection.Status
            cmd.Parameters.Add(statusParam) |> ignore
            let linkedParam = cmd.CreateParameter()
            linkedParam.ParameterName <- "$7"
            linkedParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            linkedParam.Value <- linkedAccountIdsToJsonb connection.LinkedAccountIds
            cmd.Parameters.Add(linkedParam) |> ignore
            cmd.Parameters.AddWithValue("$8", connection.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$9", connection.UpdatedAt.UtcDateTime) |> ignore
            let lastSyncedParam = cmd.CreateParameter()
            lastSyncedParam.ParameterName <- "$10"
            lastSyncedParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.TimestampTz
            match connection.LastSyncedAt with
            | Some d -> lastSyncedParam.Value <- d.UtcDateTime
            | None -> lastSyncedParam.Value <- DBNull.Value
            cmd.Parameters.Add(lastSyncedParam) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return connection.Id
        }

    let updateAsync (factory: IDbConnectionFactory) (connection: DataFeedConnection) =
        task {
            let ctx = { TenantId = connection.TenantId; UserId = connection.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """UPDATE data_feed_connections SET
                       provider_metadata = $1,
                       credential_ref = $2,
                       status = $3,
                       linked_account_ids = $4,
                       updated_at = $5,
                       last_synced_at = $6
                   WHERE id = $7"""
            let metaParam = cmd.CreateParameter()
            metaParam.ParameterName <- "$1"
            metaParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            metaParam.Value <- providerMetadataToJsonb connection.Metadata
            cmd.Parameters.Add(metaParam) |> ignore
            cmd.Parameters.AddWithValue("$2", connection.CredentialRef) |> ignore
            let statusParam = cmd.CreateParameter()
            statusParam.ParameterName <- "$3"
            statusParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            statusParam.Value <- connectionStatusToJsonb connection.Status
            cmd.Parameters.Add(statusParam) |> ignore
            let linkedParam = cmd.CreateParameter()
            linkedParam.ParameterName <- "$4"
            linkedParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            linkedParam.Value <- linkedAccountIdsToJsonb connection.LinkedAccountIds
            cmd.Parameters.Add(linkedParam) |> ignore
            cmd.Parameters.AddWithValue("$5", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            let lastSyncedParam = cmd.CreateParameter()
            lastSyncedParam.ParameterName <- "$6"
            lastSyncedParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.TimestampTz
            match connection.LastSyncedAt with
            | Some d -> lastSyncedParam.Value <- d.UtcDateTime
            | None -> lastSyncedParam.Value <- DBNull.Value
            cmd.Parameters.Add(lastSyncedParam) |> ignore
            cmd.Parameters.AddWithValue("$7", connection.Id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IDataFeedConnectionRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IDataFeedConnectionRepository with
            member _.GetAsync(id) = getAsync factory (requireCtx()) id
            member _.GetByItemIdAsync(itemId) = getByItemIdAsync factory itemId
            member _.ListAsync() = listAsync factory (requireCtx())
            member _.CreateAsync(connection) = createAsync factory connection
            member _.UpdateAsync(connection) = updateAsync factory connection
        }
