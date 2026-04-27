module BitThicket.Steward.Api.Test.TransactionEndpointsTests

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

// ── Test helpers ───────────────────────────────────────────────────────────

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
    services.AddSingleton<ICategoryRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        CategoryRepository.create f accessor) |> ignore
    services.AddSingleton<ITransactionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        TransactionRepository.create f accessor) |> ignore
    services.AddHttpContextAccessor() |> ignore
    services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Response.Body <- new MemoryStream()
    ctx

let private createHttpContextWithAuth (factory: IDbConnectionFactory) (token: string) =
    let ctx = createHttpContext factory
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"
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

let private registerAndGetToken (factory: IDbConnectionFactory) (email: string) =
    task {
        let regCtx = createHttpContext factory
        setJsonBody regCtx $"{{\"email\":\"{email}\",\"password\":\"password\",\"displayName\":\"User\",\"tenantDisplayName\":\"Tenant\"}}"
        do! Auth.registerHandler regCtx
        let regDoc = readResponseJson regCtx
        return regDoc.RootElement.GetProperty("accessToken").GetString()
    }

let private seedCategory (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (categoryId: Guid) (name: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO categories (id, tenant_id, user_id, name, parent_id, is_system, created_at)
           VALUES ($1, $2, $3, $4, NULL, false, now())"""
    cmd.Parameters.AddWithValue("$1", categoryId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", name) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) (name: string) (currency: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO accounts (
               id, tenant_id, user_id, name, account_type, currency,
               institution_name, external_id, credit_card_info,
               is_on_budget, is_active, created_at, updated_at
           ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, now(), now())"""
    cmd.Parameters.AddWithValue("$1", accountId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", name) |> ignore
    cmd.Parameters.AddWithValue("$5", "checking") |> ignore
    cmd.Parameters.AddWithValue("$6", currency) |> ignore
    cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$10", true) |> ignore
    cmd.Parameters.AddWithValue("$11", true) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedTransaction (conn: NpgsqlConnection) (tenantId: Guid) (accountId: Guid) (txnId: Guid) (amountMinor: int64) (occurredAt: DateTimeOffset) (source: string) (status: string) (createdAt: DateTimeOffset) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO transactions (
               id, tenant_id, account_id, occurred_at, posted_at,
               amount_minor, currency, description, merchant, memo,
               category_id, source, external_id, matched_transaction_id, transfer_account_id,
               status, match_confidence, sync_event_id, created_at, updated_at, deleted_at
           ) VALUES ($1, $2, $3, $4, NULL, $5, 'USD', 'Test', 'Merchant', NULL, NULL,
                     $6::jsonb, NULL, NULL, NULL, $7, NULL, NULL, $8, $8, NULL)"""
    cmd.Parameters.AddWithValue("$1", txnId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", accountId) |> ignore
    cmd.Parameters.AddWithValue("$4", occurredAt.UtcDateTime) |> ignore
    cmd.Parameters.AddWithValue("$5", amountMinor) |> ignore
    let sourceParam = cmd.CreateParameter()
    sourceParam.ParameterName <- "$6"
    sourceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
    sourceParam.Value <- box $"""{{"type":"{source}"}}"""
    cmd.Parameters.Add(sourceParam) |> ignore
    cmd.Parameters.AddWithValue("$7", status) |> ignore
    cmd.Parameters.AddWithValue("$8", createdAt.UtcDateTime) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Tests ──────────────────────────────────────────────────────────────────

type TransactionEndpointsTests() =

    [<Fact>]
    member _.``POST /api/transactions creates a manual transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txncreate@example.com"
            let regDoc =
                let ctx = createHttpContextWithAuth factory token
                setJsonBody ctx """{"email":"x","password":"x","displayName":"x","tenantDisplayName":"x"}"""
                Auth.registerHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
                readResponseJson ctx

            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountDoc = readResponseJson ctx
            let accountId = Guid.Parse(accountDoc.RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"Coffee"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx

            test <@ createCtx.Response.StatusCode = 201 @>
            let doc = readResponseJson createCtx
            test <@ doc.RootElement.GetProperty("description").GetString() = "Coffee" @>
            test <@ doc.RootElement.GetProperty("amount").GetDecimal() = -50.00m @>
            test <@ doc.RootElement.GetProperty("currency").GetString() = "USD" @>
            test <@ doc.RootElement.GetProperty("status").GetString() = "cleared" @>
            test <@ doc.RootElement.GetProperty("source").GetString() = "manual" @>
        }

    [<Fact>]
    member _.``POST /api/transactions validates currency mismatch``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txncurr@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountDoc = readResponseJson ctx
            let accountId = Guid.Parse(accountDoc.RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"EUR","description":"Coffee"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx

            test <@ createCtx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``POST /api/transactions validates transfer account not found``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txntransfer@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountDoc = readResponseJson ctx
            let accountId = Guid.Parse(accountDoc.RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"Transfer","transferAccountId":"{Guid.NewGuid()}"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx

            test <@ createCtx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``POST /api/transactions validates transfer account currency mismatch``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txntransfercurr@example.com"
            let ctx1 = createHttpContextWithAuth factory token
            setJsonBody ctx1 """{"name":"USD Account","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx1
            let usdId = Guid.Parse((readResponseJson ctx1).RootElement.GetProperty("id").GetString())

            let ctx2 = createHttpContextWithAuth factory token
            setJsonBody ctx2 """{"name":"EUR Account","accountType":"checking","currency":"EUR"}"""
            do! AccountEndpoints.createAccountHandler ctx2
            let eurId = Guid.Parse((readResponseJson ctx2).RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{usdId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"Transfer","transferAccountId":"{eurId}"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx

            test <@ createCtx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``POST /api/transactions validates category not found``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txncat@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountId = Guid.Parse((readResponseJson ctx).RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"Coffee","categoryId":"{Guid.NewGuid()}"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx

            test <@ createCtx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``GET /api/transactions/{id} returns transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnget@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountId = Guid.Parse((readResponseJson ctx).RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"Coffee"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx
            let txnId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let getCtx = createHttpContextWithAuth factory token
            do! TransactionEndpoints.getTransactionHandler txnId getCtx

            test <@ getCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson getCtx
            test <@ doc.RootElement.GetProperty("description").GetString() = "Coffee" @>
        }

    [<Fact>]
    member _.``GET /api/transactions/{id} returns 404 for non-existent``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnget404@example.com"
            let getCtx = createHttpContextWithAuth factory token
            do! TransactionEndpoints.getTransactionHandler (Guid.NewGuid()) getCtx

            test <@ getCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``GET /api/transactions lists with pagination``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnlist@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountId = Guid.Parse((readResponseJson ctx).RootElement.GetProperty("id").GetString())

            // Insert 5 transactions with distinct occurredAt values
            let accessor =
                { new ITenantContextAccessor with
                    member _.Context =
                        let jwt = Jwt.tryReadToken testAuthConfig.JwtSecret None testAuthConfig.Issuer testAuthConfig.Audience token
                        match jwt with
                        | Jwt.ValidationResult.Valid doc ->
                            let tid = Guid.Parse(doc.RootElement.GetProperty("tid").GetString())
                            let uid = Guid.Parse(doc.RootElement.GetProperty("sub").GetString())
                            Some { TenantId = tid; UserId = uid }
                        | _ -> None }
            let repo = TransactionRepository.create factory accessor

            for i in 1..5 do
                let txn: Transaction = {
                    Id = Guid.NewGuid()
                    TenantId = accessor.Context.Value.TenantId
                    AccountId = accountId
                    OccurredAt = DateTimeOffset(2026, 4, i, 0, 0, 0, TimeSpan.Zero)
                    PostedAt = None
                    Amount = { Amount = -decimal i * 10m; CurrencyCode = "USD" }
                    Description = $"Tx {i}"
                    Merchant = None
                    Memo = None
                    CategoryId = None
                    Status = TransactionStatus.Cleared
                    Source = TransactionSource.Manual
                    ExternalId = None
                    MatchedTransactionId = None
                    TransferAccountId = None
                    MatchConfidence = None
                    SyncEventId = None
                    DeletedAt = None
                    CreatedAt = DateTimeOffset.UtcNow
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                let! _ = repo.CreateAsync(txn)
                ()

            // List with limit 2
            let listCtx = createHttpContextWithAuth factory token
            listCtx.Request.QueryString <- Microsoft.AspNetCore.Http.QueryString($"?accountId={accountId}&limit=2")
            do! TransactionEndpoints.listTransactionsHandler listCtx

            test <@ listCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson listCtx
            let items = doc.RootElement.GetProperty("items").EnumerateArray() |> Seq.toList
            test <@ items.Length = 2 @>
            test <@ items.[0].GetProperty("description").GetString() = "Tx 5" @>
            test <@ items.[1].GetProperty("description").GetString() = "Tx 4" @>
            let hasNextCursor, _ = doc.RootElement.TryGetProperty("nextCursor")
            test <@ hasNextCursor @>

            // Page 2 using cursor
            let cursor = doc.RootElement.GetProperty("nextCursor").GetString()
            let listCtx2 = createHttpContextWithAuth factory token
            listCtx2.Request.QueryString <- Microsoft.AspNetCore.Http.QueryString($"?accountId={accountId}&limit=2&cursor={Uri.EscapeDataString(cursor)}")
            do! TransactionEndpoints.listTransactionsHandler listCtx2

            let doc2 = readResponseJson listCtx2
            let items2 = doc2.RootElement.GetProperty("items").EnumerateArray() |> Seq.toList
            test <@ items2.Length = 2 @>
            test <@ items2.[0].GetProperty("description").GetString() = "Tx 3" @>
            test <@ items2.[1].GetProperty("description").GetString() = "Tx 2" @>
        }

    [<Fact>]
    member _.``GET /api/transactions requires from and to when no accountId``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnlistval@example.com"
            let listCtx = createHttpContextWithAuth factory token
            do! TransactionEndpoints.listTransactionsHandler listCtx

            test <@ listCtx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``PATCH /api/transactions updates mutable fields``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnpatch@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountId = Guid.Parse((readResponseJson ctx).RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"Coffee"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx
            let txnId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx """{"description":"Updated","merchant":"Starbucks","notes":"Morning coffee"}"""
            do! TransactionEndpoints.updateTransactionHandler txnId patchCtx

            test <@ patchCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson patchCtx
            test <@ doc.RootElement.GetProperty("description").GetString() = "Updated" @>
            test <@ doc.RootElement.GetProperty("merchant").GetString() = "Starbucks" @>
            test <@ doc.RootElement.GetProperty("notes").GetString() = "Morning coffee" @>
        }

    [<Fact>]
    member _.``PATCH /api/transactions rejects amount and dates for feed entries``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnfeedpatch@example.com"
            let jwtDoc =
                let ctx = createHttpContextWithAuth factory token
                setJsonBody ctx """{"email":"x","password":"x","displayName":"x","tenantDisplayName":"x"}"""
                Auth.registerHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
                readResponseJson ctx
            let tenantId = Guid.Parse(jwtDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(jwtDoc.RootElement.GetProperty("userId").GetString())

            use seedConn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            let txnId = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txnId -5000L (DateTimeOffset.UtcNow.AddDays(-1.0)) "data_feed" "cleared" DateTimeOffset.UtcNow

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx """{"amountMinor":-10000}"""
            do! TransactionEndpoints.updateTransactionHandler txnId patchCtx

            test <@ patchCtx.Response.StatusCode = 422 @>
        }

    [<Fact>]
    member _.``PATCH /api/transactions allows amount and dates for manual entries within 30-day window``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnmanualpatch@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountId = Guid.Parse((readResponseJson ctx).RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"Coffee"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx
            let txnId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx """{"amountMinor":-10000,"occurredAt":"2026-04-02T00:00:00Z"}"""
            do! TransactionEndpoints.updateTransactionHandler txnId patchCtx

            test <@ patchCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson patchCtx
            test <@ doc.RootElement.GetProperty("amount").GetDecimal() = -100.00m @>
            test <@ doc.RootElement.GetProperty("occurredAt").GetString() = "2026-04-02T00:00:00+00:00" @>
        }

    [<Fact>]
    member _.``PATCH /api/transactions rejects amount and dates for manual entries outside 30-day window``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnoldpatch@example.com"
            let jwtDoc =
                let ctx = createHttpContextWithAuth factory token
                setJsonBody ctx """{"email":"x","password":"x","displayName":"x","tenantDisplayName":"x"}"""
                Auth.registerHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
                readResponseJson ctx
            let tenantId = Guid.Parse(jwtDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(jwtDoc.RootElement.GetProperty("userId").GetString())

            use seedConn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            let txnId = Guid.NewGuid()
            let oldCreated = DateTimeOffset.UtcNow.AddDays(-31.0)
            seedTransaction seedConn tenantId accountId txnId -5000L (DateTimeOffset.UtcNow.AddDays(-1.0)) "manual" "cleared" oldCreated

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx """{"amountMinor":-10000}"""
            do! TransactionEndpoints.updateTransactionHandler txnId patchCtx

            test <@ patchCtx.Response.StatusCode = 422 @>
        }

    [<Fact>]
    member _.``DELETE /api/transactions/{id} soft-deletes and returns 204``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txndelete@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountId = Guid.Parse((readResponseJson ctx).RootElement.GetProperty("id").GetString())

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"Coffee"}}"""
            do! TransactionEndpoints.createTransactionHandler createCtx
            let txnId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let delCtx = createHttpContextWithAuth factory token
            do! TransactionEndpoints.deleteTransactionHandler txnId delCtx
            test <@ delCtx.Response.StatusCode = 204 @>

            let getCtx = createHttpContextWithAuth factory token
            do! TransactionEndpoints.getTransactionHandler txnId getCtx
            test <@ getCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``DELETE /api/transactions/{id} returns 404 for non-existent``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txndel404@example.com"
            let delCtx = createHttpContextWithAuth factory token
            do! TransactionEndpoints.deleteTransactionHandler (Guid.NewGuid()) delCtx

            test <@ delCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot access tenant B transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! tokenA = registerAndGetToken factory "tenantA_txn@example.com"
            let! tokenB = registerAndGetToken factory "tenantB_txn@example.com"

            let createCtx = createHttpContextWithAuth factory tokenB
            setJsonBody createCtx """{"name":"B Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler createCtx
            let accountId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let txnCtx = createHttpContextWithAuth factory tokenB
            setJsonBody txnCtx $"""{{"accountId":"{accountId}","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"B Coffee"}}"""
            do! TransactionEndpoints.createTransactionHandler txnCtx
            let txnId = Guid.Parse((readResponseJson txnCtx).RootElement.GetProperty("id").GetString())

            let getCtx = createHttpContextWithAuth factory tokenA
            do! TransactionEndpoints.getTransactionHandler txnId getCtx
            test <@ getCtx.Response.StatusCode = 404 @>

            let patchCtx = createHttpContextWithAuth factory tokenA
            setJsonBody patchCtx """{"description":"Hacked"}"""
            do! TransactionEndpoints.updateTransactionHandler txnId patchCtx
            test <@ patchCtx.Response.StatusCode = 404 @>

            let delCtx = createHttpContextWithAuth factory tokenA
            do! TransactionEndpoints.deleteTransactionHandler txnId delCtx
            test <@ delCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``Unauthenticated requests return 401``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let listCtx = createHttpContext factory
            do! TransactionEndpoints.listTransactionsHandler listCtx
            test <@ listCtx.Response.StatusCode = 401 @>

            let getCtx = createHttpContext factory
            do! TransactionEndpoints.getTransactionHandler (Guid.NewGuid()) getCtx
            test <@ getCtx.Response.StatusCode = 401 @>

            let createCtx = createHttpContext factory
            setJsonBody createCtx """{"accountId":"00000000-0000-0000-0000-000000000001","occurredAt":"2026-04-01T00:00:00Z","amountMinor":-5000,"currency":"USD","description":"X"}"""
            do! TransactionEndpoints.createTransactionHandler createCtx
            test <@ createCtx.Response.StatusCode = 401 @>

            let patchCtx = createHttpContext factory
            setJsonBody patchCtx """{"description":"X"}"""
            do! TransactionEndpoints.updateTransactionHandler (Guid.NewGuid()) patchCtx
            test <@ patchCtx.Response.StatusCode = 401 @>

            let delCtx = createHttpContext factory
            do! TransactionEndpoints.deleteTransactionHandler (Guid.NewGuid()) delCtx
            test <@ delCtx.Response.StatusCode = 401 @>
        }

    [<Fact>]
    member _.``Pagination test: 250 inserted txns paged through deterministically``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "txnpaginate@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx
            let accountId = Guid.Parse((readResponseJson ctx).RootElement.GetProperty("id").GetString())

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context =
                        let jwt = Jwt.tryReadToken testAuthConfig.JwtSecret None testAuthConfig.Issuer testAuthConfig.Audience token
                        match jwt with
                        | Jwt.ValidationResult.Valid doc ->
                            let tid = Guid.Parse(doc.RootElement.GetProperty("tid").GetString())
                            let uid = Guid.Parse(doc.RootElement.GetProperty("sub").GetString())
                            Some { TenantId = tid; UserId = uid }
                        | _ -> None }
            let repo = TransactionRepository.create factory accessor

            // Insert 250 transactions, newest first
            for i in 1..250 do
                let txn: Transaction = {
                    Id = Guid.NewGuid()
                    TenantId = accessor.Context.Value.TenantId
                    AccountId = accountId
                    OccurredAt = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-float i)
                    PostedAt = None
                    Amount = { Amount = -decimal i; CurrencyCode = "USD" }
                    Description = $"Tx {i}"
                    Merchant = None
                    Memo = None
                    CategoryId = None
                    Status = TransactionStatus.Cleared
                    Source = TransactionSource.Manual
                    ExternalId = None
                    MatchedTransactionId = None
                    TransferAccountId = None
                    MatchConfidence = None
                    SyncEventId = None
                    DeletedAt = None
                    CreatedAt = DateTimeOffset.UtcNow
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                let! _ = repo.CreateAsync(txn)
                ()

            let mutable cursor: string option = None
            let mutable totalCount = 0
            let mutable pageCount = 0
            let mutable keepGoing = true

            while keepGoing do
                let listCtx = createHttpContextWithAuth factory token
                let qs =
                    match cursor with
                    | Some c -> $"?accountId={accountId}&limit=50&cursor={Uri.EscapeDataString(c)}"
                    | None -> $"?accountId={accountId}&limit=50"
                listCtx.Request.QueryString <- Microsoft.AspNetCore.Http.QueryString(qs)
                do! TransactionEndpoints.listTransactionsHandler listCtx

                test <@ listCtx.Response.StatusCode = 200 @>
                let doc = readResponseJson listCtx
                let items = doc.RootElement.GetProperty("items").EnumerateArray() |> Seq.toList
                totalCount <- totalCount + items.Length
                pageCount <- pageCount + 1

                match doc.RootElement.TryGetProperty("nextCursor") with
                | true, el when el.ValueKind <> JsonValueKind.Null ->
                    cursor <- Some(el.GetString())
                | _ ->
                    cursor <- None
                    keepGoing <- false

            test <@ totalCount = 250 @>
            test <@ pageCount = 5 @>
        }
