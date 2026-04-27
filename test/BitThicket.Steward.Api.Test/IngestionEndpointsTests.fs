module BitThicket.Steward.Api.Test.IngestionEndpointsTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open Falco
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

// ── Test helpers ─────────────────────────────────────────────────────────────

let private sharedContainer : PostgreSqlContainer option =
    try
        let c =
            PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build()
        c.StartAsync().GetAwaiter().GetResult()
        Some c
    with _ ->
        None

let private connectionString () =
    match sharedContainer with
    | Some c -> c.GetConnectionString()
    | None -> null

let private canConnect () : bool =
    let cs = connectionString ()
    if String.IsNullOrWhiteSpace(cs) then false
    else
        try
            use dataSource = NpgsqlDataSource.Create(cs)
            use conn = dataSource.OpenConnection()
            true
        with _ -> false

let private runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

let private testAuthConfig = {
    JwtSecret = "test-secret-key-for-unit-tests-only-do-not-use-in-production"
    JwtSecretPrevious = None
    Issuer = "steward"
    Audience = "steward-api"
}

let private createHttpContext (factory: IDbConnectionFactory) =
    let services = ServiceCollection()
    services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
    services.AddSingleton<AuthConfig>(testAuthConfig) |> ignore
    services.AddSingleton<IAccountRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        AccountRepository.create f accessor) |> ignore
    services.AddSingleton<ITransactionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        TransactionRepository.create f accessor) |> ignore
    services.AddSingleton<IDataFeedConnectionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        DataFeedConnectionRepository.create f accessor) |> ignore
    services.AddSingleton<ISyncEventRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        SyncEventRepository.create f accessor) |> ignore
    services.AddHttpContextAccessor() |> ignore
    services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Response.Body <- new MemoryStream()
    ctx

let private createHttpContextWithApiKey (factory: IDbConnectionFactory) (apiKey: string) =
    let ctx = createHttpContext factory
    ctx.Request.Headers["x-api-key"] <- apiKey
    ctx

let private setJsonBody (ctx: HttpContext) (json: string) =
    let bytes = Encoding.UTF8.GetBytes(json)
    ctx.Request.Body <- new MemoryStream(bytes)
    ctx.Request.ContentType <- "application/json"
    ctx.Request.ContentLength <- int64 bytes.Length

let private readResponse (ctx: HttpContext) =
    ctx.Response.Body.Position <- 0L
    use reader = new StreamReader(ctx.Response.Body)
    reader.ReadToEnd()

let private readResponseJson (ctx: HttpContext) =
    let json = readResponse ctx
    JsonDocument.Parse(json)

// ── Seed helpers ─────────────────────────────────────────────────────────────

let private seedTenantUserAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO tenants (id, display_name, created_at, updated_at)
           VALUES ($1, $2, now(), now());
           INSERT INTO users (id, email, password_hash, display_name, created_at, updated_at)
           VALUES ($3, $4, 'hash', 'User', now(), now());
           INSERT INTO user_tenant_memberships (user_id, tenant_id, role, created_at)
           VALUES ($3, $1, 'owner', now());
           INSERT INTO accounts (id, tenant_id, user_id, name, account_type, currency,
                                 institution_name, external_id, is_on_budget, is_active,
                                 created_at, updated_at)
           VALUES ($5, $1, $3, 'Test Checking', 'checking', 'USD',
                   'Test Bank', 'ext-acc-1', true, true, now(), now());"""
    cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$2", $"Tenant {tenantId.ToString()[..7]}") |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", $"{userId}@test.com") |> ignore
    cmd.Parameters.AddWithValue("$5", accountId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedApiKey (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (scopes: string list) =
    let keyId = Guid.NewGuid()
    let fullKey, prefix, hash = ApiKeyRepository.generateKey()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO api_keys (id, tenant_id, user_id, display_name, key_hash, key_prefix,
                                 role, scopes, expires_at, last_used_at, revoked_at, created_at)
           VALUES ($1, $2, $3, 'Ingestion Key', $4, $5, 'service', $6, NULL, NULL, NULL, now())"""
    cmd.Parameters.AddWithValue("$1", keyId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", hash) |> ignore
    cmd.Parameters.AddWithValue("$5", prefix) |> ignore
    let scopeParam = cmd.CreateParameter()
    scopeParam.ParameterName <- "$6"
    scopeParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Array ||| NpgsqlTypes.NpgsqlDbType.Text
    scopeParam.Value <- (scopes |> List.toArray)
    cmd.Parameters.Add(scopeParam) |> ignore
    cmd.ExecuteNonQuery() |> ignore
    fullKey

let private seedConnection (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (connectionId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO data_feed_connections (
               id, tenant_id, user_id, provider_metadata, credential_ref, status,
               linked_account_ids, created_at, updated_at)
           VALUES ($1, $2, $3, '{"case":"Plaid","fields":{"itemId":"item_123","institutionId":"ins_123"}}'::jsonb,
                   'vault-ref-1', '{"case":"Active"}'::jsonb, '[]'::jsonb, now(), now())"""
    cmd.Parameters.AddWithValue("$1", connectionId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Tests ────────────────────────────────────────────────────────────────────

type IngestionEndpointsTests() =

    [<Fact>]
    member _.``POST /internal/ingestion/upsert creates transactions and sync event``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            let connectionId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantUserAccount seedConn tenantId userId accountId
            seedConnection seedConn tenantId userId connectionId
            let apiKey = seedApiKey seedConn tenantId userId ["ingestion:write"]

            let ctx = createHttpContextWithApiKey factory apiKey
            setJsonBody ctx $"""{{
                "connectionId": "{connectionId}",
                "transactions": [
                    {{
                        "externalId": "txn-001",
                        "accountId": "{accountId}",
                        "occurredAt": "2026-04-26T10:00:00Z",
                        "postedAt": "2026-04-26T10:00:00Z",
                        "amount": -12.34,
                        "currency": "USD",
                        "description": "Starbucks",
                        "merchant": "Starbucks",
                        "memo": null
                    }}
                ]
            }}"""
            do! IngestionEndpoints.upsertHandler ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx
            test <@ doc.RootElement.GetProperty("added").GetInt32() = 1 @>
            test <@ doc.RootElement.GetProperty("updated").GetInt32() = 0 @>
            let syncEventId = Guid.Parse(doc.RootElement.GetProperty("syncEventId").GetString())

            // Verify sync event exists
            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let syncRepo = SyncEventRepository.create factory accessor
            let! syncOpt = syncRepo.GetAsync(syncEventId)
            test <@ syncOpt |> Option.isSome @>
            test <@ syncOpt.Value.TransactionsAdded = 1 @>
            test <@ syncOpt.Value.TransactionsUpdated = 0 @>

            // Verify transaction exists
            let txnRepo = TransactionRepository.create factory accessor
            let! txnOpt = txnRepo.GetByExternalIdAsync "txn-001" accountId
            test <@ txnOpt |> Option.isSome @>
            test <@ txnOpt.Value.Description = "Starbucks" @>
            test <@ txnOpt.Value.Status = TransactionStatus.Cleared @>
        }

    [<Fact>]
    member _.``POST /internal/ingestion/upsert updates existing transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            let connectionId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantUserAccount seedConn tenantId userId accountId
            seedConnection seedConn tenantId userId connectionId
            let apiKey = seedApiKey seedConn tenantId userId ["ingestion:write"]

            // First upsert
            let ctx1 = createHttpContextWithApiKey factory apiKey
            setJsonBody ctx1 $"""{{
                "connectionId": "{connectionId}",
                "transactions": [
                    {{
                        "externalId": "txn-002",
                        "accountId": "{accountId}",
                        "occurredAt": "2026-04-26T10:00:00Z",
                        "postedAt": null,
                        "amount": -12.34,
                        "currency": "USD",
                        "description": "Starbucks",
                        "merchant": "Starbucks",
                        "memo": null
                    }}
                ]
            }}"""
            do! IngestionEndpoints.upsertHandler ctx1
            test <@ ctx1.Response.StatusCode = 200 @>

            // Second upsert with updated description and postedAt
            let ctx2 = createHttpContextWithApiKey factory apiKey
            setJsonBody ctx2 $"""{{
                "connectionId": "{connectionId}",
                "transactions": [
                    {{
                        "externalId": "txn-002",
                        "accountId": "{accountId}",
                        "occurredAt": "2026-04-26T10:00:00Z",
                        "postedAt": "2026-04-27T10:00:00Z",
                        "amount": -12.34,
                        "currency": "USD",
                        "description": "Starbucks #2",
                        "merchant": "Starbucks",
                        "memo": "updated"
                    }}
                ]
            }}"""
            do! IngestionEndpoints.upsertHandler ctx2
            test <@ ctx2.Response.StatusCode = 200 @>
            let doc2 = readResponseJson ctx2
            test <@ doc2.RootElement.GetProperty("added").GetInt32() = 0 @>
            test <@ doc2.RootElement.GetProperty("updated").GetInt32() = 1 @>

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let txnRepo = TransactionRepository.create factory accessor
            let! txnOpt = txnRepo.GetByExternalIdAsync "txn-002" accountId
            test <@ txnOpt |> Option.isSome @>
            test <@ txnOpt.Value.Description = "Starbucks #2" @>
            test <@ txnOpt.Value.PostedAt |> Option.isSome @>
        }

    [<Fact>]
    member _.``POST /internal/ingestion/upsert returns 401 without auth``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let ctx = createHttpContext factory
            setJsonBody ctx """{"connectionId":"00000000-0000-0000-0000-000000000000","transactions":[]}"""
            do! IngestionEndpoints.upsertHandler ctx
            test <@ ctx.Response.StatusCode = 401 @>
        }

    [<Fact>]
    member _.``POST /internal/ingestion/upsert returns 403 without ingestion scope``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            use cmd = seedConn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO tenants (id, display_name, created_at, updated_at)
                   VALUES ($1, $2, now(), now());
                   INSERT INTO users (id, email, password_hash, display_name, created_at, updated_at)
                   VALUES ($3, $4, 'hash', 'User', now(), now());
                   INSERT INTO user_tenant_memberships (user_id, tenant_id, role, created_at)
                   VALUES ($3, $1, 'owner', now());"""
            cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
            cmd.Parameters.AddWithValue("$2", $"Tenant {tenantId.ToString()[..7]}") |> ignore
            cmd.Parameters.AddWithValue("$3", userId) |> ignore
            cmd.Parameters.AddWithValue("$4", $"{userId}@test.com") |> ignore
            cmd.ExecuteNonQuery() |> ignore

            let apiKey = seedApiKey seedConn tenantId userId [] // no scopes

            let ctx = createHttpContextWithApiKey factory apiKey
            setJsonBody ctx """{"connectionId":"00000000-0000-0000-0000-000000000000","transactions":[]}"""
            do! IngestionEndpoints.upsertHandler ctx
            test <@ ctx.Response.StatusCode = 403 @>
        }

    [<Fact>]
    member _.``POST /internal/ingestion/upsert returns 404 for unknown connection``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantUserAccount seedConn tenantId userId accountId
            let apiKey = seedApiKey seedConn tenantId userId ["ingestion:write"]

            let ctx = createHttpContextWithApiKey factory apiKey
            setJsonBody ctx $"""{{
                "connectionId": "{Guid.NewGuid()}",
                "transactions": []
            }}"""
            do! IngestionEndpoints.upsertHandler ctx
            test <@ ctx.Response.StatusCode = 404 @>
        }
