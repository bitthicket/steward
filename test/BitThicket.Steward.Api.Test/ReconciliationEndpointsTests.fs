module BitThicket.Steward.Api.Test.ReconciliationEndpointsTests

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
    services.AddSingleton<ITransactionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        TransactionRepository.create f accessor) |> ignore
    services.AddSingleton<IReconciliationRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        ReconciliationRepository.create f accessor) |> ignore
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

let private seedAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO accounts (id, tenant_id, user_id, name, account_type, currency,
               institution_name, external_id, credit_card_info, is_on_budget, is_active,
               deleted_at, created_at, updated_at)
           VALUES ($1, $2, $3, 'Test Account', 'checking', 'USD',
               NULL, NULL, NULL, true, true, NULL, now(), now())"""
    cmd.Parameters.AddWithValue("$1", accountId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedTransaction (conn: NpgsqlConnection) (tenantId: Guid) (accountId: Guid) (txnId: Guid) (amountMinor: int64) (postedAt: DateTimeOffset) (status: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO transactions (
               id, tenant_id, account_id, occurred_at, posted_at,
               amount_minor, currency, description, merchant, memo,
               category_id, source, external_id, matched_transaction_id, transfer_account_id,
               status, match_confidence, sync_event_id, created_at, updated_at)
           VALUES ($1, $2, $3, $4, $5, $6, 'USD', 'Test txn', NULL, NULL,
               NULL, '{\"type\":\"manual\"}', NULL, NULL, NULL,
               $7, NULL, NULL, now(), now())"""
    cmd.Parameters.AddWithValue("$1", txnId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", accountId) |> ignore
    cmd.Parameters.AddWithValue("$4", postedAt.UtcDateTime) |> ignore
    cmd.Parameters.AddWithValue("$5", postedAt.UtcDateTime) |> ignore
    cmd.Parameters.AddWithValue("$6", amountMinor) |> ignore
    cmd.Parameters.AddWithValue("$7", status) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Tests ──────────────────────────────────────────────────────────────────

type ReconciliationEndpointsTests() =

    [<Fact>]
    member _.``POST /api/reconciliations creates reconciliation and returns candidates``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "recon-create@example.com"
            let tenantId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.TenantId
            let userId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.UserId

            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId

            let txn1 = Guid.NewGuid()
            let txn2 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"
            seedTransaction seedConn tenantId accountId txn2 200L (DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)) "cleared"

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"{{\"accountId\":\"{accountId}\",\"statementDate\":\"2026-04-15\",\"statementBalanceMinor\":300,\"currency\":\"USD\"}}"
            do! ReconciliationEndpoints.createReconciliationHandler createCtx

            test <@ createCtx.Response.StatusCode = 201 @>
            let doc = readResponseJson createCtx
            Assert.Equal(accountId.ToString(), doc.RootElement.GetProperty("reconciliation").GetProperty("accountId").GetString())
            let candidates = doc.RootElement.GetProperty("candidateTransactions").EnumerateArray() |> Seq.toList
            test <@ candidates.Length = 1 @>
            Assert.Equal(txn1.ToString(), candidates.[0].GetProperty("id").GetString())
        }

    [<Fact>]
    member _.``GET /api/reconciliations/{id} returns reconciliation with diff``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "recon-get@example.com"
            let tenantId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.TenantId
            let userId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.UserId

            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"{{\"accountId\":\"{accountId}\",\"statementDate\":\"2026-04-15\",\"statementBalanceMinor\":100,\"currency\":\"USD\"}}"
            do! ReconciliationEndpoints.createReconciliationHandler createCtx
            let createDoc = readResponseJson createCtx
            let reconId = Guid.Parse(createDoc.RootElement.GetProperty("reconciliation").GetProperty("id").GetString())

            let getCtx = createHttpContextWithAuth factory token
            do! ReconciliationEndpoints.getReconciliationHandler reconId getCtx

            test <@ getCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson getCtx
            Assert.Equal(reconId.ToString(), doc.RootElement.GetProperty("id").GetString())
            test <@ doc.RootElement.GetProperty("diffMinor").GetInt64() = 0L @>
        }

    [<Fact>]
    member _.``PATCH /api/reconciliations/{id}/transactions adjusts included transactions``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "recon-patch@example.com"
            let tenantId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.TenantId
            let userId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.UserId

            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId

            let txn1 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"{{\"accountId\":\"{accountId}\",\"statementDate\":\"2026-04-15\",\"statementBalanceMinor\":100,\"currency\":\"USD\"}}"
            do! ReconciliationEndpoints.createReconciliationHandler createCtx
            let createDoc = readResponseJson createCtx
            let reconId = Guid.Parse(createDoc.RootElement.GetProperty("reconciliation").GetProperty("id").GetString())

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx $"{{\"included\":[\"{txn1}\"],\"excluded\":[]}}"
            do! ReconciliationEndpoints.updateTransactionsHandler reconId patchCtx

            test <@ patchCtx.Response.StatusCode = 200 @>

            let getCtx = createHttpContextWithAuth factory token
            do! ReconciliationEndpoints.getReconciliationHandler reconId getCtx
            let getDoc = readResponseJson getCtx
            let included = getDoc.RootElement.GetProperty("includedTransactions").EnumerateArray() |> Seq.toList
            test <@ included.Length = 1 @>
        }

    [<Fact>]
    member _.``POST /api/reconciliations/{id}/complete succeeds when balanced``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "recon-complete@example.com"
            let tenantId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.TenantId
            let userId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.UserId

            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId

            let txn1 = Guid.NewGuid()
            let txn2 = Guid.NewGuid()
            let txn3 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"
            seedTransaction seedConn tenantId accountId txn2 100L (DateTimeOffset(2026, 4, 11, 0, 0, 0, TimeSpan.Zero)) "cleared"
            seedTransaction seedConn tenantId accountId txn3 100L (DateTimeOffset(2026, 4, 12, 0, 0, 0, TimeSpan.Zero)) "cleared"

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"{{\"accountId\":\"{accountId}\",\"statementDate\":\"2026-04-15\",\"statementBalanceMinor\":300,\"currency\":\"USD\"}}"
            do! ReconciliationEndpoints.createReconciliationHandler createCtx
            let createDoc = readResponseJson createCtx
            let reconId = Guid.Parse(createDoc.RootElement.GetProperty("reconciliation").GetProperty("id").GetString())

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx $"{{\"included\":[\"{txn1}\",\"{txn2}\",\"{txn3}\"],\"excluded\":[]}}"
            do! ReconciliationEndpoints.updateTransactionsHandler reconId patchCtx

            let completeCtx = createHttpContextWithAuth factory token
            do! ReconciliationEndpoints.completeHandler reconId completeCtx

            test <@ completeCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson completeCtx
            test <@ doc.RootElement.GetProperty("status").GetString() = "completed" @>
        }

    [<Fact>]
    member _.``POST /api/reconciliations/{id}/complete returns 409 when unbalanced``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "recon-409@example.com"
            let tenantId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.TenantId
            let userId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.UserId

            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId

            let txn1 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"{{\"accountId\":\"{accountId}\",\"statementDate\":\"2026-04-15\",\"statementBalanceMinor\":500,\"currency\":\"USD\"}}"
            do! ReconciliationEndpoints.createReconciliationHandler createCtx
            let createDoc = readResponseJson createCtx
            let reconId = Guid.Parse(createDoc.RootElement.GetProperty("reconciliation").GetProperty("id").GetString())

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx $"{{\"included\":[\"{txn1}\"],\"excluded\":[]}}"
            do! ReconciliationEndpoints.updateTransactionsHandler reconId patchCtx

            let completeCtx = createHttpContextWithAuth factory token
            do! ReconciliationEndpoints.completeHandler reconId completeCtx

            test <@ completeCtx.Response.StatusCode = 409 @>
            let doc = readResponseJson completeCtx
            test <@ doc.RootElement.GetProperty("diffMinor").GetInt64() = -400L @>
        }

    [<Fact>]
    member _.``POST /api/reconciliations/{id}/complete?force=true succeeds when unbalanced``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "recon-force@example.com"
            let tenantId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.TenantId
            let userId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.UserId

            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId

            let txn1 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"{{\"accountId\":\"{accountId}\",\"statementDate\":\"2026-04-15\",\"statementBalanceMinor\":500,\"currency\":\"USD\"}}"
            do! ReconciliationEndpoints.createReconciliationHandler createCtx
            let createDoc = readResponseJson createCtx
            let reconId = Guid.Parse(createDoc.RootElement.GetProperty("reconciliation").GetProperty("id").GetString())

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx $"{{\"included\":[\"{txn1}\"],\"excluded\":[]}}"
            do! ReconciliationEndpoints.updateTransactionsHandler reconId patchCtx

            let completeCtx = createHttpContextWithAuth factory token
            completeCtx.Request.QueryString <- Microsoft.AspNetCore.Http.QueryString("?force=true")
            do! ReconciliationEndpoints.completeHandler reconId completeCtx

            test <@ completeCtx.Response.StatusCode = 200 @>
        }

    [<Fact>]
    member _.``POST /api/reconciliations/{id}/abort marks aborted``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "recon-abort@example.com"
            let tenantId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.TenantId
            let userId =
                let ctx = createHttpContextWithAuth factory token
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.UserId

            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"{{\"accountId\":\"{accountId}\",\"statementDate\":\"2026-04-15\",\"statementBalanceMinor\":100,\"currency\":\"USD\"}}"
            do! ReconciliationEndpoints.createReconciliationHandler createCtx
            let createDoc = readResponseJson createCtx
            let reconId = Guid.Parse(createDoc.RootElement.GetProperty("reconciliation").GetProperty("id").GetString())

            let abortCtx = createHttpContextWithAuth factory token
            do! ReconciliationEndpoints.abortHandler reconId abortCtx

            test <@ abortCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson abortCtx
            test <@ doc.RootElement.GetProperty("status").GetString() = "aborted" @>
        }

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot access tenant B reconciliation``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! tokenA = registerAndGetToken factory "tenantA-recon@example.com"
            let! tokenB = registerAndGetToken factory "tenantB-recon@example.com"
            let tenantB =
                let ctx = createHttpContextWithAuth factory tokenB
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.TenantId
            let userB =
                let ctx = createHttpContextWithAuth factory tokenB
                let accessor = ctx.RequestServices.GetRequiredService<ITenantContextAccessor>()
                accessor.Context.Value.UserId

            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantB userB accountId

            let createCtx = createHttpContextWithAuth factory tokenB
            setJsonBody createCtx $"{{\"accountId\":\"{accountId}\",\"statementDate\":\"2026-04-15\",\"statementBalanceMinor\":100,\"currency\":\"USD\"}}"
            do! ReconciliationEndpoints.createReconciliationHandler createCtx
            let createDoc = readResponseJson createCtx
            let reconId = Guid.Parse(createDoc.RootElement.GetProperty("reconciliation").GetProperty("id").GetString())

            // Tenant A tries GET → 404
            let getCtx = createHttpContextWithAuth factory tokenA
            do! ReconciliationEndpoints.getReconciliationHandler reconId getCtx
            test <@ getCtx.Response.StatusCode = 404 @>
        }
