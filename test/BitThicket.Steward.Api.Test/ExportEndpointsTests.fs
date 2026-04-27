module BitThicket.Steward.Api.Test.ExportEndpointsTests

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

// ── Test helpers (shared patterns from TransactionEndpointsTests) ──────────

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
    services.AddSingleton<IBudgetRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        BudgetRepository.create f accessor) |> ignore
    services.AddSingleton<IBudgetPeriodRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        BudgetPeriodRepository.create f accessor) |> ignore
    services.AddSingleton<IAttachmentStorage>(fun sp ->
        let log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalAttachmentStorage>>()
        AttachmentStorage.fromEnvironment log) |> ignore
    services.AddSingleton<IAttachmentRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        AttachmentRepository.create f accessor) |> ignore
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

let private seedCategory (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (categoryId: Guid) (name: string) (parentId: Guid option) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO categories (id, tenant_id, user_id, name, parent_id, is_system, currency, rollover_enabled, created_at)
           VALUES ($1, $2, $3, $4, $5, false, 'USD', false, now())"""
    cmd.Parameters.AddWithValue("$1", categoryId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", name) |> ignore
    match parentId with
    | Some pid -> cmd.Parameters.AddWithValue("$5", pid) |> ignore
    | None -> cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
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

let private seedTransaction (conn: NpgsqlConnection) (tenantId: Guid) (accountId: Guid) (txnId: Guid) (amountMinor: int64) (occurredAt: DateTimeOffset) (categoryId: Guid option) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO transactions (
               id, tenant_id, account_id, occurred_at, posted_at,
               amount_minor, currency, description, merchant, memo,
               category_id, source, external_id, matched_transaction_id, transfer_account_id,
               status, match_confidence, sync_event_id, created_at, updated_at, deleted_at
           ) VALUES ($1, $2, $3, $4, NULL, $5, 'USD', 'Test desc', 'Test Merchant', NULL,
                     $6, $7::jsonb, NULL, NULL, NULL, 'cleared', NULL, NULL, $8, $8, NULL)"""
    cmd.Parameters.AddWithValue("$1", txnId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", accountId) |> ignore
    cmd.Parameters.AddWithValue("$4", occurredAt.UtcDateTime) |> ignore
    cmd.Parameters.AddWithValue("$5", amountMinor) |> ignore
    match categoryId with
    | Some cid -> cmd.Parameters.AddWithValue("$6", cid) |> ignore
    | None -> cmd.Parameters.AddWithValue("$6", DBNull.Value) |> ignore
    let sourceParam = cmd.CreateParameter()
    sourceParam.ParameterName <- "$7"
    sourceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
    sourceParam.Value <- box """{"type":"manual"}"""
    cmd.Parameters.Add(sourceParam) |> ignore
    cmd.Parameters.AddWithValue("$8", occurredAt.UtcDateTime) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedBudget (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (budgetId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO budgets (
               id, tenant_id, user_id, name, style, period, currency,
               income_minor, is_active, starts_on, created_at, updated_at
           ) VALUES ($1, $2, $3, 'Test Budget', 'zero_based', 'monthly', 'USD',
                     500000, true, '2026-01-01', now(), now())"""
    cmd.Parameters.AddWithValue("$1", budgetId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedBudgetPeriod (conn: NpgsqlConnection) (tenantId: Guid) (budgetId: Guid) (periodId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO budget_periods (
               id, budget_id, tenant_id, start_date, end_date, status, created_at, updated_at
           ) VALUES ($1, $2, $3, '2026-04-01', '2026-04-30', 'Open', now(), now())"""
    cmd.Parameters.AddWithValue("$1", periodId) |> ignore
    cmd.Parameters.AddWithValue("$2", budgetId) |> ignore
    cmd.Parameters.AddWithValue("$3", tenantId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedBudgetPeriodCategory (conn: NpgsqlConnection) (periodId: Guid) (categoryId: Guid) (allocatedMinor: int64) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO budget_period_categories (
               budget_period_id, category_id, allocated_minor, opening_balance_minor,
               rollover_balance_minor, currency, rollover_enabled
           ) VALUES ($1, $2, $3, 0, 0, 'USD', false)"""
    cmd.Parameters.AddWithValue("$1", periodId) |> ignore
    cmd.Parameters.AddWithValue("$2", categoryId) |> ignore
    cmd.Parameters.AddWithValue("$3", allocatedMinor) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Tests ──────────────────────────────────────────────────────────────────

type ExportEndpointsTests() =

    [<Fact>]
    member _.``GET /api/exports/transactions.csv returns CSV with correct columns``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "exporttxn@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(regDoc.RootElement.GetProperty("userId").GetString())

            use conn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let categoryId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            seedAccount conn tenantId userId accountId "Checking" "USD"
            seedCategory conn tenantId userId categoryId "Groceries" None
            seedTransaction conn tenantId accountId txnId -1250L (DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero)) (Some categoryId)

            let ctx = createHttpContextWithAuth factory token
            ctx.Request.QueryString <- Microsoft.AspNetCore.Http.QueryString("?from=2026-04-01T00:00:00Z&to=2026-04-30T23:59:59Z")
            do! ExportEndpoints.exportTransactionsHandler ctx

            test <@ ctx.Response.StatusCode = 200 @>
            test <@ ctx.Response.ContentType = "text/csv; charset=utf-8" @>

            let csv = readResponse ctx
            let lines = csv.Split([|"\n"|], StringSplitOptions.RemoveEmptyEntries)
            test <@ lines.Length = 2 @> // header + 1 data row
            test <@ lines.[0].StartsWith("id,occurred_at,posted_at,account_name") @>
            test <@ lines.[1].Contains("Checking") @>
            test <@ lines.[1].Contains("-1250") @>
            test <@ lines.[1].Contains("Groceries") @>
        }

    [<Fact>]
    member _.``GET /api/exports/transactions.csv requires from and to when no accountId``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "exporttxnbad@example.com"

            let ctx = createHttpContextWithAuth factory token
            do! ExportEndpoints.exportTransactionsHandler ctx

            test <@ ctx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``GET /api/exports/transactions.csv returns 401 without auth``() =
        task {
            let ctx = DefaultHttpContext()
            ctx.Response.Body <- new MemoryStream()
            let services = ServiceCollection()
            services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
            services.AddHttpContextAccessor() |> ignore
            ctx.RequestServices <- services.BuildServiceProvider()

            do! ExportEndpoints.exportTransactionsHandler ctx
            test <@ ctx.Response.StatusCode = 401 @>
        }

    [<Fact>]
    member _.``GET /api/exports/accounts.csv returns CSV with balances``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "exportacct@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(regDoc.RootElement.GetProperty("userId").GetString())

            use conn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            seedAccount conn tenantId userId accountId "Savings" "USD"
            seedTransaction conn tenantId accountId txnId 50000L (DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero)) None

            let ctx = createHttpContextWithAuth factory token
            do! ExportEndpoints.exportAccountsHandler ctx

            test <@ ctx.Response.StatusCode = 200 @>
            test <@ ctx.Response.ContentType = "text/csv; charset=utf-8" @>

            let csv = readResponse ctx
            let lines = csv.Split([|"\n"|], StringSplitOptions.RemoveEmptyEntries)
            test <@ lines.Length = 2 @>
            test <@ lines.[0].StartsWith("id,name,account_type") @>
            test <@ lines.[1].Contains("Savings") @>
            test <@ lines.[1].Contains("50000") @>
        }

    [<Fact>]
    member _.``GET /api/exports/budgets/{id}/period/{periodId}.csv returns budget CSV``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "exportbudget@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(regDoc.RootElement.GetProperty("userId").GetString())

            use conn = dataSource.OpenConnection()
            let budgetId = Guid.NewGuid()
            let periodId = Guid.NewGuid()
            let categoryId = Guid.NewGuid()
            seedBudget conn tenantId userId budgetId
            seedBudgetPeriod conn tenantId budgetId periodId
            seedCategory conn tenantId userId categoryId "Food" None
            seedBudgetPeriodCategory conn periodId categoryId 20000L

            let ctx = createHttpContextWithAuth factory token
            do! ExportEndpoints.exportBudgetPeriodHandler budgetId periodId ctx

            test <@ ctx.Response.StatusCode = 200 @>
            test <@ ctx.Response.ContentType = "text/csv; charset=utf-8" @>

            let csv = readResponse ctx
            let lines = csv.Split([|"\n"|], StringSplitOptions.RemoveEmptyEntries)
            test <@ lines.Length = 2 @>
            test <@ lines.[0].StartsWith("category_name,allocated_minor") @>
            test <@ lines.[1].Contains("Food") @>
            test <@ lines.[1].Contains("20000") @>
        }

    [<Fact>]
    member _.``GET /api/exports/budgets/{id}/period/{periodId}.csv returns 404 for missing budget``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "exportbudget404@example.com"

            let ctx = createHttpContextWithAuth factory token
            do! ExportEndpoints.exportBudgetPeriodHandler (Guid.NewGuid()) (Guid.NewGuid()) ctx

            test <@ ctx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``GET /api/exports/transactions.csv includes category path for nested categories``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "exportcatpath@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(regDoc.RootElement.GetProperty("userId").GetString())

            use conn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let parentId = Guid.NewGuid()
            let childId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            seedAccount conn tenantId userId accountId "Checking" "USD"
            seedCategory conn tenantId userId parentId "Food" None
            seedCategory conn tenantId userId childId "Groceries" (Some parentId)
            seedTransaction conn tenantId accountId txnId -1000L (DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero)) (Some childId)

            let ctx = createHttpContextWithAuth factory token
            ctx.Request.QueryString <- Microsoft.AspNetCore.Http.QueryString("?from=2026-04-01T00:00:00Z&to=2026-04-30T23:59:59Z")
            do! ExportEndpoints.exportTransactionsHandler ctx

            let csv = readResponse ctx
            let lines = csv.Split([|"\n"|], StringSplitOptions.RemoveEmptyEntries)
            test <@ lines.[1].Contains("Food > Groceries") @>
        }
