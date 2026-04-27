namespace BitThicket.Steward.Api

open System
open System.Data.Common
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

type IDataFeedConnectionRepository =
    abstract GetAsync : id:Guid -> Task<DataFeedConnection option>
    abstract CreateAsync : connection:DataFeedConnection -> Task<Guid>
    abstract UpdateAsync : connection:DataFeedConnection -> Task<unit>

module DataFeedConnectionRepository =

    let private jsonOptions =
        let opts = JsonSerializerOptions()
        opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        opts

    let private providerMetadataToJson (metadata: ProviderMetadata) : string =
        let obj = JsonObject()
        match metadata with
        | ProviderMetadata.Akoya(customerId, institutionId) ->
            obj["type"] <- JsonValue.Create("akoya")
            obj["customerId"] <- JsonValue.Create(customerId)
            obj["institutionId"] <- JsonValue.Create(institutionId)
        | ProviderMetadata.Plaid(itemId, institutionId) ->
            obj["type"] <- JsonValue.Create("plaid")
            obj["itemId"] <- JsonValue.Create(itemId)
            obj["institutionId"] <- JsonValue.Create(institutionId)
        | ProviderMetadata.MX(memberGuid, institutionCode) ->
            obj["type"] <- JsonValue.Create("mx")
            obj["memberGuid"] <- JsonValue.Create(memberGuid)
            obj["institutionCode"] <- JsonValue.Create(institutionCode)
        | ProviderMetadata.Yodlee(providerAccountId, loginFormId) ->
            obj["type"] <- JsonValue.Create("yodlee")
            obj["providerAccountId"] <- JsonValue.Create(providerAccountId)
            match loginFormId with
            | Some id -> obj["loginFormId"] <- JsonValue.Create(id)
            | None -> ()
        | ProviderMetadata.Intuit(realmId, companyName) ->
            obj["type"] <- JsonValue.Create("intuit")
            obj["realmId"] <- JsonValue.Create(realmId)
            match companyName with
            | Some name -> obj["companyName"] <- JsonValue.Create(name)
            | None -> ()
        | ProviderMetadata.Manual ->
            obj["type"] <- JsonValue.Create("manual")
        obj.ToJsonString()

    let private providerMetadataFromJson (json: string) : ProviderMetadata =
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        match root.GetProperty("type").GetString() with
        | "akoya" ->
            ProviderMetadata.Akoya(
                root.GetProperty("customerId").GetString(),
                root.GetProperty("institutionId").GetString())
        | "plaid" ->
            ProviderMetadata.Plaid(
                root.GetProperty("itemId").GetString(),
                root.GetProperty("institutionId").GetString())
        | "mx" ->
            ProviderMetadata.MX(
                root.GetProperty("memberGuid").GetString(),
                root.GetProperty("institutionCode").GetString())
        | "yodlee" ->
            ProviderMetadata.Yodlee(
                root.GetProperty("providerAccountId").GetString(),
                match root.TryGetProperty("loginFormId") with
                | true, p when p.ValueKind <> JsonValueKind.Null -> Some(p.GetString())
                | _ -> None)
        | "intuit" ->
            ProviderMetadata.Intuit(
                root.GetProperty("realmId").GetString(),
                match root.TryGetProperty("companyName") with
                | true, p when p.ValueKind <> JsonValueKind.Null -> Some(p.GetString())
                | _ -> None)
        | _ -> ProviderMetadata.Manual

    let private connectionStatusToJson (status: ConnectionStatus) : string =
        let obj = JsonObject()
        match status with
        | ConnectionStatus.Active -> obj["type"] <- JsonValue.Create("active")
        | ConnectionStatus.NeedsReauth -> obj["type"] <- JsonValue.Create("needs_reauth")
        | ConnectionStatus.Disabled -> obj["type"] <- JsonValue.Create("disabled")
        | ConnectionStatus.Error message ->
            obj["type"] <- JsonValue.Create("error")
            obj["message"] <- JsonValue.Create(message)
        obj.ToJsonString()

    let private connectionStatusFromJson (json: string) : ConnectionStatus =
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        match root.GetProperty("type").GetString() with
        | "active" -> ConnectionStatus.Active
        | "needs_reauth" -> ConnectionStatus.NeedsReauth
        | "disabled" -> ConnectionStatus.Disabled
        | "error" -> ConnectionStatus.Error(root.GetProperty("message").GetString())
        | _ -> ConnectionStatus.Active

    let private linkedAccountIdsToJson (ids: Guid list) : string =
        JsonSerializer.Serialize(ids, jsonOptions)

    let private linkedAccountIdsFromJson (json: string) : Guid list =
        JsonSerializer.Deserialize<Guid list>(json, jsonOptions)

    let private mapConnection (reader: DbDataReader) : DataFeedConnection =
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            UserId = reader.GetGuid(2)
            Metadata = providerMetadataFromJson (reader.GetString(3))
            CredentialRef = reader.GetString(4)
            Status = connectionStatusFromJson (reader.GetString(5))
            LinkedAccountIds = linkedAccountIdsFromJson (reader.GetString(6))
            CreatedAt = Sql.dateTimeOffset reader 7
            UpdatedAt = Sql.dateTimeOffset reader 8
        }

    let getAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, provider_metadata, credential_ref, status,
                          linked_account_ids, created_at, updated_at
                   FROM data_feed_connections WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
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
                       id, tenant_id, user_id, provider, provider_metadata, credential_ref, status,
                       linked_account_ids, created_at, updated_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)"""
            cmd.Parameters.AddWithValue("$1", connection.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", connection.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", connection.UserId) |> ignore
            let providerName =
                match DataFeedConnection.providerOf connection.Metadata with
                | DataFeedProvider.Akoya -> "akoya"
                | DataFeedProvider.Plaid -> "plaid"
                | DataFeedProvider.MX -> "mx"
                | DataFeedProvider.Yodlee -> "yodlee"
                | DataFeedProvider.Intuit -> "intuit"
                | DataFeedProvider.Manual -> "manual"
            cmd.Parameters.AddWithValue("$4", providerName) |> ignore
            let metaParam = cmd.CreateParameter()
            metaParam.ParameterName <- "$5"
            metaParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            metaParam.Value <- providerMetadataToJson connection.Metadata
            cmd.Parameters.Add(metaParam) |> ignore
            cmd.Parameters.AddWithValue("$6", connection.CredentialRef) |> ignore
            let statusParam = cmd.CreateParameter()
            statusParam.ParameterName <- "$7"
            statusParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            statusParam.Value <- connectionStatusToJson connection.Status
            cmd.Parameters.Add(statusParam) |> ignore
            let linkedParam = cmd.CreateParameter()
            linkedParam.ParameterName <- "$8"
            linkedParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            linkedParam.Value <- linkedAccountIdsToJson connection.LinkedAccountIds
            cmd.Parameters.Add(linkedParam) |> ignore
            cmd.Parameters.AddWithValue("$9", connection.CreatedAt.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$10", connection.UpdatedAt.UtcDateTime) |> ignore
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
                       provider_metadata = $1, credential_ref = $2, status = $3,
                       linked_account_ids = $4, updated_at = $5
                   WHERE id = $6"""
            let metaParam = cmd.CreateParameter()
            metaParam.ParameterName <- "$1"
            metaParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            metaParam.Value <- providerMetadataToJson connection.Metadata
            cmd.Parameters.Add(metaParam) |> ignore
            cmd.Parameters.AddWithValue("$2", connection.CredentialRef) |> ignore
            let statusParam = cmd.CreateParameter()
            statusParam.ParameterName <- "$3"
            statusParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            statusParam.Value <- connectionStatusToJson connection.Status
            cmd.Parameters.Add(statusParam) |> ignore
            let linkedParam = cmd.CreateParameter()
            linkedParam.ParameterName <- "$4"
            linkedParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            linkedParam.Value <- linkedAccountIdsToJson connection.LinkedAccountIds
            cmd.Parameters.Add(linkedParam) |> ignore
            cmd.Parameters.AddWithValue("$5", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$6", connection.Id) |> ignore
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
            member _.CreateAsync(connection) = createAsync factory connection
            member _.UpdateAsync(connection) = updateAsync factory connection
        }
