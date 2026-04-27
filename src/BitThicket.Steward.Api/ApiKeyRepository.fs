namespace BitThicket.Steward.Api

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Npgsql
open BitThicket.Steward.Api.Domain

/// Repository for tenant-scoped API keys.
type IApiKeyRepository =
    abstract GetByIdAsync : id:Guid -> Task<ApiKey option>
    abstract ListByTenantAsync : unit -> Task<ApiKey list>
    abstract CreateAsync : apiKey:ApiKey -> Task<ApiKey>
    abstract RevokeAsync : id:Guid -> Task<bool>
    abstract UpdateLastUsedAsync : id:Guid -> Task<unit>

module ApiKeyRepository =

    let private hashKey (key: string) : string =
        let bytes = Encoding.UTF8.GetBytes(key)
        use sha = SHA256.Create()
        let hash = sha.ComputeHash(bytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    /// Generate a new API key (prefix + random part) and its SHA-256 hash.
    /// Returns (fullKey, keyPrefix, keyHash).
    let generateKey () : string * string * string =
        let prefix = "sk_steward_"
        let randomBytes = RandomNumberGenerator.GetBytes(32)
        let b64 = Convert.ToBase64String(randomBytes)
        let randomPart =
            b64.Replace("+", "-").Replace("/", "_").Replace("=", "").Substring(0, 32)
        let fullKey = prefix + randomPart
        let keyPrefix = fullKey.Substring(0, Math.Min(8, fullKey.Length))
        let keyHash = hashKey fullKey
        fullKey, keyPrefix, keyHash

    let private mapApiKey (reader: System.Data.Common.DbDataReader) : ApiKey =
        let readStringArray (r: System.Data.Common.DbDataReader) (ord: int) : string list =
            if r.IsDBNull(ord) then []
            else
                let arr = r.GetValue(ord) :?> string[]
                arr |> Array.toList
        {
            Id = reader.GetGuid(0)
            TenantId = reader.GetGuid(1)
            UserId = reader.GetGuid(2)
            DisplayName = reader.GetString(3)
            KeyHash = reader.GetString(4)
            KeyPrefix = reader.GetString(5)
            Role = reader.GetString(6)
            Scopes = readStringArray reader 7
            ExpiresAt = Sql.nullableDateTimeOffset reader 8
            LastUsedAt = Sql.nullableDateTimeOffset reader 9
            RevokedAt = Sql.nullableDateTimeOffset reader 10
            CreatedAt = Sql.dateTimeOffset reader 11
        }

    let getByIdAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, display_name, key_hash, key_prefix,
                          role, scopes, expires_at, last_used_at, revoked_at, created_at
                   FROM api_keys WHERE id = $1"""
            cmd.Parameters.AddWithValue("$1", id) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            return if hasRow then Some(mapApiKey reader) else None
        }

    let listByTenantAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, display_name, key_hash, key_prefix,
                          role, scopes, expires_at, last_used_at, revoked_at, created_at
                   FROM api_keys
                   ORDER BY created_at DESC"""
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let keys = ResizeArray<ApiKey>()
            while! reader.ReadAsync() do
                keys.Add(mapApiKey reader)
            return keys |> Seq.toList
        }

    let createAsync (factory: IDbConnectionFactory) (apiKey: ApiKey) =
        task {
            let ctx = { TenantId = apiKey.TenantId; UserId = apiKey.UserId }
            use! conn = factory.OpenForTenantAsync(ctx)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO api_keys (
                       id, tenant_id, user_id, display_name, key_hash, key_prefix,
                       role, scopes, expires_at, last_used_at, revoked_at, created_at
                   ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)"""
            cmd.Parameters.AddWithValue("$1", apiKey.Id) |> ignore
            cmd.Parameters.AddWithValue("$2", apiKey.TenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", apiKey.UserId) |> ignore
            cmd.Parameters.AddWithValue("$4", apiKey.DisplayName) |> ignore
            cmd.Parameters.AddWithValue("$5", apiKey.KeyHash) |> ignore
            cmd.Parameters.AddWithValue("$6", apiKey.KeyPrefix) |> ignore
            cmd.Parameters.AddWithValue("$7", apiKey.Role) |> ignore
            let scopeParam = cmd.CreateParameter()
            scopeParam.ParameterName <- "$8"
            scopeParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Array ||| NpgsqlTypes.NpgsqlDbType.Text
            scopeParam.Value <- (apiKey.Scopes |> List.toArray)
            cmd.Parameters.Add(scopeParam) |> ignore
            match apiKey.ExpiresAt with
            | Some dt -> cmd.Parameters.AddWithValue("$9", dt.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
            match apiKey.LastUsedAt with
            | Some dt -> cmd.Parameters.AddWithValue("$10", dt.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$10", DBNull.Value) |> ignore
            match apiKey.RevokedAt with
            | Some dt -> cmd.Parameters.AddWithValue("$11", dt.UtcDateTime) |> ignore
            | None -> cmd.Parameters.AddWithValue("$11", DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("$12", apiKey.CreatedAt.UtcDateTime) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return apiKey
        }

    let revokeAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "UPDATE api_keys SET revoked_at = $1 WHERE id = $2 AND revoked_at IS NULL"
            cmd.Parameters.AddWithValue("$1", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$2", id) |> ignore
            let! rows = cmd.ExecuteNonQueryAsync()
            return rows > 0
        }

    let updateLastUsedAsync (factory: IDbConnectionFactory) (tenantContext: TenantContext) (id: Guid) =
        task {
            use! conn = factory.OpenForTenantAsync(tenantContext)
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "UPDATE api_keys SET last_used_at = $1 WHERE id = $2"
            cmd.Parameters.AddWithValue("$1", DateTimeOffset.UtcNow.UtcDateTime) |> ignore
            cmd.Parameters.AddWithValue("$2", id) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return ()
        }

    /// Look up an API key by its full plaintext value, returning the key record
    /// plus tenant context if valid (not revoked, not expired). Bypasses RLS
    /// because this uses the key hash directly.
    let tryFindByKeyAsync (factory: IDbConnectionFactory) (key: string) : Task<(ApiKey * TenantContext) option> =
        task {
            use! conn = factory.OpenAsync()
            use cmd = conn.CreateCommand()
            let keyHash = hashKey key
            cmd.CommandText <-
                """SELECT id, tenant_id, user_id, display_name, key_hash, key_prefix,
                          role, scopes, expires_at, last_used_at, revoked_at, created_at
                   FROM api_keys
                   WHERE key_hash = $1"""
            cmd.Parameters.AddWithValue("$1", keyHash) |> ignore
            let! reader = cmd.ExecuteReaderAsync()
            use reader = reader
            let! hasRow = reader.ReadAsync()
            if not hasRow then return None
            else
                let apiKey = mapApiKey reader
                let now = DateTimeOffset.UtcNow
                if apiKey.RevokedAt.IsSome then return None
                elif apiKey.ExpiresAt.IsSome && apiKey.ExpiresAt.Value <= now then return None
                else
                    let tc = { TenantId = apiKey.TenantId; UserId = apiKey.UserId }
                    return Some (apiKey, tc)
        }

    let create (factory: IDbConnectionFactory) (accessor: ITenantContextAccessor) : IApiKeyRepository =
        let requireCtx () =
            match accessor.Context with
            | Some ctx -> ctx
            | None -> failwith "No tenant context available for the current operation"

        { new IApiKeyRepository with
            member _.GetByIdAsync(id) = getByIdAsync factory (requireCtx()) id
            member _.ListByTenantAsync() = listByTenantAsync factory (requireCtx())
            member _.CreateAsync(apiKey) = createAsync factory apiKey
            member _.RevokeAsync(id) = revokeAsync factory (requireCtx()) id
            member _.UpdateLastUsedAsync(id) = updateLastUsedAsync factory (requireCtx()) id
        }
