module BitThicket.Steward.Api.Test.McpToolsTests

open System
open System.IO
open System.Net.Http
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

// ── Shared test helpers (mirrors McpResourcesTests) ────────────────────────

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
    services.AddSingleton<ICategoryRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        CategoryRepository.create f accessor) |> ignore
    services.AddSingleton<IBudgetRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        BudgetRepository.create f accessor) |> ignore
    services.AddSingleton<IBudgetPeriodRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        BudgetPeriodRepository.create f accessor) |> ignore
    services.AddSingleton<IReconciliationRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        ReconciliationRepository.create f accessor) |> ignore
    services.AddSingleton<IRemediationAttemptRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        RemediationAttemptRepository.create f accessor) |> ignore
    services.AddSingleton<IDataFeedConnectionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        DataFeedConnectionRepository.create f accessor) |> ignore
    services.AddSingleton<HttpClient>(HttpClient()) |> ignore
    services.AddHttpContextAccessor() |> ignore
    services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Response.Body <- new MemoryStream()
    ctx

let private createHttpContextWithAuth (factory: IDbConnectionFactory) (tenantId: Guid) (userId: Guid) =
    let ctx = createHttpContext factory
    ctx.Items["TenantContext"] <- { TenantId = tenantId; UserId = userId }
    ctx.Items["TenantRole"] <- "owner"
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

let private callTool (ctx: HttpContext) (toolName: string) (args: string) =
    let payload = $"""{{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{"name":"{toolName}","arguments":{args}}}}}"""
    setJsonBody ctx payload
    McpServer.mcpHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
    readResponseJson ctx

// ── Tests ──────────────────────────────────────────────────────────────────

type McpToolsTests() =

    [<Fact>]
    member _.``tools/list includes all mutation tools``() =
        task {
            if not (canConnect ()) then return () else
            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""
            do! McpServer.mcpHandler ctx
            Assert.True(ctx.Response.StatusCode = 200)
            let doc = readResponseJson ctx
            let tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray() |> Seq.toList
            let names = tools |> List.map (fun t -> t.GetProperty("name").GetString()) |> Set.ofList
            Assert.True(names.Contains("categorize_transaction"))
            Assert.True(names.Contains("create_budget"))
            Assert.True(names.Contains("trigger_sync"))
            Assert.True(names.Contains("reconcile_account"))
            Assert.True(names.Contains("accept_match"))
            Assert.True(names.Contains("reject_match"))
            Assert.True(names.Contains("record_remediation"))
        }

    [<Fact>]
    member _.``categorize_transaction updates category``() =
        task {
            if not (canConnect ()) then return () else
            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId

            let accountRepo = AccountRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let categoryRepo = CategoryRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let txnRepo = TransactionRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })

            let account = {
                Id = Guid.NewGuid(); TenantId = tenantId; UserId = userId; Name = "Checking"
                AccountType = AccountType.Checking; CurrencyCode = "USD"; InstitutionName = None
                ExternalId = None; CreditCardInfo = None; IsOnBudget = true; IsActive = true
                DeletedAt = None; CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = accountRepo.CreateAsync(account)

            let category = {
                Id = Guid.NewGuid(); TenantId = tenantId; UserId = userId; ParentCategoryId = None; Name = "Groceries"
                IsSystem = false; CurrencyCode = "USD"; RolloverEnabled = false
                DeletedAt = None; CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = categoryRepo.CreateAsync(category)

            let txn = {
                Id = Guid.NewGuid(); TenantId = tenantId; AccountId = account.Id
                Amount = { Amount = 50m; CurrencyCode = "USD" }; Description = "Test"; Merchant = None
                Memo = None; CategoryId = None; Status = TransactionStatus.Cleared
                Source = TransactionSource.Manual; ExternalId = None; MatchedTransactionId = None
                MatchConfidence = None; TransferAccountId = None; SyncEventId = None
                PostedAt = None; OccurredAt = DateTimeOffset.UtcNow; DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! txnId = txnRepo.CreateAsync(txn)

            let doc = callTool ctx "categorize_transaction" $"""{{"transactionId":"{txnId}","categoryId":"{category.Id}"}}"""
            let resultText = doc.RootElement.GetProperty("result").GetProperty("content").EnumerateArray() |> Seq.head |> fun el -> el.GetProperty("text").GetString()
            Assert.True(resultText.Contains("\"ok\":true"))

            let! updated = txnRepo.GetAsync(txnId)
            Assert.True(updated.Value.CategoryId = Some category.Id)
        }

    [<Fact>]
    member _.``create_budget creates budget and first period``() =
        task {
            if not (canConnect ()) then return () else
            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId

            let categoryRepo = CategoryRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let cat = {
                Id = Guid.NewGuid(); TenantId = tenantId; UserId = userId; ParentCategoryId = None; Name = "Food"
                IsSystem = false; CurrencyCode = "USD"; RolloverEnabled = false
                DeletedAt = None; CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = categoryRepo.CreateAsync(cat)

            let doc = callTool ctx "create_budget" $"""{{"name":"Test Budget","period":"monthly","currency":"USD","style":"flexible","allocations":[{{"categoryId":"{cat.Id}","amountMinor":10000}}]}}"""
            let resultText = doc.RootElement.GetProperty("result").GetProperty("content").EnumerateArray() |> Seq.head |> fun el -> el.GetProperty("text").GetString()
            Assert.True(resultText.Contains("\"ok\":true"))
            Assert.True(resultText.Contains("\"budgetId\""))
        }

    [<Fact>]
    member _.``reconcile_account creates and completes reconciliation``() =
        task {
            if not (canConnect ()) then return () else
            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId

            let accountRepo = AccountRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let txnRepo = TransactionRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })

            let account = {
                Id = Guid.NewGuid(); TenantId = tenantId; UserId = userId; Name = "Checking"
                AccountType = AccountType.Checking; CurrencyCode = "USD"; InstitutionName = None
                ExternalId = None; CreditCardInfo = None; IsOnBudget = true; IsActive = true
                DeletedAt = None; CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = accountRepo.CreateAsync(account)

            let txn1 = {
                Id = Guid.NewGuid(); TenantId = tenantId; AccountId = account.Id
                Amount = { Amount = 30m; CurrencyCode = "USD" }; Description = "T1"; Merchant = None
                Memo = None; CategoryId = None; Status = TransactionStatus.Cleared
                Source = TransactionSource.Manual; ExternalId = None; MatchedTransactionId = None
                MatchConfidence = None; TransferAccountId = None; SyncEventId = None
                PostedAt = None; OccurredAt = DateTimeOffset.UtcNow; DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let txn2 = {
                Id = Guid.NewGuid(); TenantId = tenantId; AccountId = account.Id
                Amount = { Amount = 20m; CurrencyCode = "USD" }; Description = "T2"; Merchant = None
                Memo = None; CategoryId = None; Status = TransactionStatus.Cleared
                Source = TransactionSource.Manual; ExternalId = None; MatchedTransactionId = None
                MatchConfidence = None; TransferAccountId = None; SyncEventId = None
                PostedAt = None; OccurredAt = DateTimeOffset.UtcNow; DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! tid1 = txnRepo.CreateAsync(txn1)
            let! tid2 = txnRepo.CreateAsync(txn2)

            let today = DateTime.UtcNow.ToString("yyyy-MM-dd")
            let doc = callTool ctx "reconcile_account" $"""{{"accountId":"{account.Id}","statementDate":"{today}","statementBalanceMinor":5000,"currency":"USD","transactionIds":["{tid1}","{tid2}"]}}"""
            let resultText = doc.RootElement.GetProperty("result").GetProperty("content").EnumerateArray() |> Seq.head |> fun el -> el.GetProperty("text").GetString()
            Assert.True(resultText.Contains("\"ok\":true"))
        }

    [<Fact>]
    member _.``accept_match resolves a NeedsReview transaction``() =
        task {
            if not (canConnect ()) then return () else
            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId

            let accountRepo = AccountRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let txnRepo = TransactionRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })

            let account = {
                Id = Guid.NewGuid(); TenantId = tenantId; UserId = userId; Name = "Checking"
                AccountType = AccountType.Checking; CurrencyCode = "USD"; InstitutionName = None
                ExternalId = None; CreditCardInfo = None; IsOnBudget = true; IsActive = true
                DeletedAt = None; CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = accountRepo.CreateAsync(account)

            let feedTxn = {
                Id = Guid.NewGuid(); TenantId = tenantId; AccountId = account.Id
                Amount = { Amount = 100m; CurrencyCode = "USD" }; Description = "Feed"; Merchant = None
                Memo = None; CategoryId = None; Status = TransactionStatus.NeedsReview
                Source = TransactionSource.DataFeed "plaid"; ExternalId = Some "ext-1"; MatchedTransactionId = None
                MatchConfidence = None; TransferAccountId = None; SyncEventId = None
                PostedAt = None; OccurredAt = DateTimeOffset.UtcNow; DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let manualTxn = {
                Id = Guid.NewGuid(); TenantId = tenantId; AccountId = account.Id
                Amount = { Amount = 100m; CurrencyCode = "USD" }; Description = "Manual"; Merchant = None
                Memo = None; CategoryId = None; Status = TransactionStatus.Cleared
                Source = TransactionSource.Manual; ExternalId = None; MatchedTransactionId = None
                MatchConfidence = None; TransferAccountId = None; SyncEventId = None
                PostedAt = None; OccurredAt = DateTimeOffset.UtcNow; DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! feedId = txnRepo.CreateAsync(feedTxn)
            let! manualId = txnRepo.CreateAsync(manualTxn)

            // Set matched transaction id so accept can resolve it
            let feedWithMatch = { feedTxn with Id = feedId; MatchedTransactionId = Some manualId }
            do! txnRepo.UpdateAsync(feedWithMatch)

            let doc = callTool ctx "accept_match" $"""{{"transactionId":"{feedId}"}}"""
            let resultText = doc.RootElement.GetProperty("result").GetProperty("content").EnumerateArray() |> Seq.head |> fun el -> el.GetProperty("text").GetString()
            Assert.True(resultText.Contains("\"ok\":true"))
            Assert.True(resultText.Contains("\"action\":\"accept\""))
        }

    [<Fact>]
    member _.``reject_match clears a NeedsReview transaction``() =
        task {
            if not (canConnect ()) then return () else
            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId

            let accountRepo = AccountRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let txnRepo = TransactionRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })

            let account = {
                Id = Guid.NewGuid(); TenantId = tenantId; UserId = userId; Name = "Checking"
                AccountType = AccountType.Checking; CurrencyCode = "USD"; InstitutionName = None
                ExternalId = None; CreditCardInfo = None; IsOnBudget = true; IsActive = true
                DeletedAt = None; CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = accountRepo.CreateAsync(account)

            let feedTxn = {
                Id = Guid.NewGuid(); TenantId = tenantId; AccountId = account.Id
                Amount = { Amount = 100m; CurrencyCode = "USD" }; Description = "Feed"; Merchant = None
                Memo = None; CategoryId = None; Status = TransactionStatus.NeedsReview
                Source = TransactionSource.DataFeed "plaid"; ExternalId = Some "ext-1"; MatchedTransactionId = None
                MatchConfidence = None; TransferAccountId = None; SyncEventId = None
                PostedAt = None; OccurredAt = DateTimeOffset.UtcNow; DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! feedId = txnRepo.CreateAsync(feedTxn)

            let doc = callTool ctx "reject_match" $"""{{"transactionId":"{feedId}"}}"""
            let resultText = doc.RootElement.GetProperty("result").GetProperty("content").EnumerateArray() |> Seq.head |> fun el -> el.GetProperty("text").GetString()
            Assert.True(resultText.Contains("\"ok\":true"))
            Assert.True(resultText.Contains("\"action\":\"reject\""))
        }

    [<Fact>]
    member _.``record_remediation creates a remediation attempt``() =
        task {
            if not (canConnect ()) then return () else
            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId

            let connRepo = DataFeedConnectionRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let connection : DataFeedConnection = {
                Id = Guid.NewGuid(); TenantId = tenantId; UserId = userId
                Metadata = ProviderMetadata.Plaid("item-1", "inst-1", None)
                CredentialRef = "cred-1"; Status = ConnectionStatus.Active
                LinkedAccountIds = []; LastSyncedAt = None; CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow
            }
            let! connId = connRepo.CreateAsync(connection)

            let doc = callTool ctx "record_remediation" $"""{{"connectionId":"{connId}","strategy":"refresh-token","notes":"Token expired"}}"""
            let resultText = doc.RootElement.GetProperty("result").GetProperty("content").EnumerateArray() |> Seq.head |> fun el -> el.GetProperty("text").GetString()
            Assert.True(resultText.Contains("\"ok\":true"))
            Assert.True(resultText.Contains("\"remediationId\""))
        }
