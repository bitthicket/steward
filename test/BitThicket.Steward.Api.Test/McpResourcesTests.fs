module BitThicket.Steward.Api.Test.McpResourcesTests

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

// ── Test helpers (shared with McpToolsTests) ───────────────────────────────

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
    services.AddSingleton<IDataFeedConnectionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        DataFeedConnectionRepository.create f accessor) |> ignore
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

// ── Tests ──────────────────────────────────────────────────────────────────

type McpResourcesTests() =

    [<Fact>]
    member _.``resources/list returns all resources``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx """{"jsonrpc":"2.0","id":1,"method":"resources/list"}"""

            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let resources = doc.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray() |> Seq.toList
            let uris = resources |> List.map (fun r -> r.GetProperty("uri").GetString()) |> Set.ofList
            Assert.True( uris.Contains("steward://accounts") )
            Assert.True( uris.Contains("steward://accounts/{id}") )
            Assert.True( uris.Contains("steward://transactions") )
            Assert.True( uris.Contains("steward://transactions/{id}") )
            Assert.True( uris.Contains("steward://budgets") )
            Assert.True( uris.Contains("steward://budgets/{id}") )
            Assert.True( uris.Contains("steward://budgets/{id}/periods/{periodId}/report") )
            Assert.True( uris.Contains("steward://categories") )
        }

    [<Fact>]
    member _.``resources/read accounts list returns accounts``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            let accountRepo = AccountRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let account = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Checking"
                AccountType = AccountType.Checking
                CurrencyCode = "USD"
                InstitutionName = None
                ExternalId = None
                CreditCardInfo = None
                IsOnBudget = true
                IsActive = true
                DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = accountRepo.CreateAsync(account)

            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"steward://accounts"}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let contents = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray() |> Seq.toList
            Assert.True(contents.Length = 1)
            let text = contents.[0].GetProperty("text").GetString()
            Assert.True(text.Contains("Checking"))
            Assert.True(text.Contains("\"accounts\""))
        }

    [<Fact>]
    member _.``resources/read account by id returns single account``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            let accountRepo = AccountRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let account = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Savings"
                AccountType = AccountType.Savings
                CurrencyCode = "USD"
                InstitutionName = None
                ExternalId = None
                CreditCardInfo = None
                IsOnBudget = true
                IsActive = true
                DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = accountRepo.CreateAsync(account)

            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx $"""{{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{{"uri":"steward://accounts/{account.Id}"}}}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let contents = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray() |> Seq.toList
            let text = contents.[0].GetProperty("text").GetString()
            Assert.True(text.Contains("Savings"))
            Assert.True(text.Contains("\"id\""))
        }

    [<Fact>]
    member _.``resources/read account by id returns not found for missing``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx $"""{{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{{"uri":"steward://accounts/{Guid.NewGuid()}"}}}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let contents = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray() |> Seq.toList
            let text = contents.[0].GetProperty("text").GetString()
            Assert.True(text.Contains("not found"))
        }

    [<Fact>]
    member _.``resources/read transactions list returns transactions``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            let accountRepo = AccountRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let account = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Checking"
                AccountType = AccountType.Checking
                CurrencyCode = "USD"
                InstitutionName = None
                ExternalId = None
                CreditCardInfo = None
                IsOnBudget = true
                IsActive = true
                DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = accountRepo.CreateAsync(account)

            let txnRepo = TransactionRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let txn = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                AccountId = account.Id
                Amount = { Amount = 42.50m; CurrencyCode = "USD" }
                Description = "Grocery store"
                Merchant = Some "Whole Foods"
                Memo = None
                CategoryId = None
                Status = TransactionStatus.Cleared
                Source = TransactionSource.Manual
                ExternalId = None
                MatchedTransactionId = None
                TransferAccountId = None
                MatchConfidence = None
                SyncEventId = None
                PostedAt = Some DateTimeOffset.UtcNow
                OccurredAt = DateTimeOffset.UtcNow
                DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = txnRepo.CreateAsync(txn)

            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"steward://transactions"}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let contents = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray() |> Seq.toList
            let text = contents.[0].GetProperty("text").GetString()
            Assert.True(text.Contains("Grocery store"))
            Assert.True(text.Contains("\"transactions\""))
        }

    [<Fact>]
    member _.``resources/read categories list returns categories``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            let catRepo = CategoryRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let category = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Groceries"
                ParentCategoryId = None
                IsSystem = false
                CurrencyCode = "USD"
                RolloverEnabled = false
                DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = catRepo.CreateAsync(category)

            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"steward://categories"}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let contents = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray() |> Seq.toList
            let text = contents.[0].GetProperty("text").GetString()
            Assert.True(text.Contains("Groceries"))
            Assert.True(text.Contains("\"categories\""))
        }

    [<Fact>]
    member _.``resources/read categories tree returns tree structure``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            let catRepo = CategoryRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let parent = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Food"
                ParentCategoryId = None
                IsSystem = false
                CurrencyCode = "USD"
                RolloverEnabled = false
                DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let child = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Groceries"
                ParentCategoryId = Some parent.Id
                IsSystem = false
                CurrencyCode = "USD"
                RolloverEnabled = false
                DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = catRepo.CreateAsync(parent)
            let! _ = catRepo.CreateAsync(child)

            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"steward://categories?tree=true"}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let contents = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray() |> Seq.toList
            let text = contents.[0].GetProperty("text").GetString()
            Assert.True(text.Contains("Food"))
            Assert.True(text.Contains("Groceries"))
            Assert.True(text.Contains("\"children\""))
        }

    [<Fact>]
    member _.``resources/read budgets list returns budgets``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            let budgetRepo = BudgetRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let budget = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Monthly Budget"
                Style = BudgetingStyle.ZeroBased
                Period = BudgetPeriod.Monthly
                CurrencyCode = "USD"
                Income = { Amount = 5000m; CurrencyCode = "USD" }
                IsActive = true
                StartsOn = DateOnly.FromDateTime(DateTime.UtcNow)
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = budgetRepo.CreateAsync(budget)

            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"steward://budgets"}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let contents = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray() |> Seq.toList
            let text = contents.[0].GetProperty("text").GetString()
            Assert.True(text.Contains("Monthly Budget"))
            Assert.True(text.Contains("\"budgets\""))
        }

    [<Fact>]
    member _.``resources/read budget report returns report``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            let budgetRepo = BudgetRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let periodRepo = BudgetPeriodRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })
            let catRepo = CategoryRepository.create factory ({ new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } })

            let budget = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Monthly Budget"
                Style = BudgetingStyle.ZeroBased
                Period = BudgetPeriod.Monthly
                CurrencyCode = "USD"
                Income = { Amount = 5000m; CurrencyCode = "USD" }
                IsActive = true
                StartsOn = DateOnly.FromDateTime(DateTime.UtcNow)
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = budgetRepo.CreateAsync(budget)

            let category = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                UserId = userId
                Name = "Groceries"
                ParentCategoryId = None
                IsSystem = false
                CurrencyCode = "USD"
                RolloverEnabled = false
                DeletedAt = None
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = catRepo.CreateAsync(category)

            let period = {
                Id = Guid.NewGuid()
                BudgetId = budget.Id
                TenantId = tenantId
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow)
                EndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1))
                Status = BudgetPeriodStatus.Open
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let alloc = {
                BudgetPeriodId = period.Id
                CategoryId = category.Id
                AllocatedAmount = { Amount = 500m; CurrencyCode = "USD" }
                OpeningBalance = { Amount = 0m; CurrencyCode = "USD" }
                RolloverBalance = { Amount = 0m; CurrencyCode = "USD" }
                RolloverEnabled = false
            }
            let! _ = periodRepo.CreatePeriodAsync(period, [alloc])

            let ctx = createHttpContextWithAuth factory tenantId userId
            setJsonBody ctx $"""{{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{{"uri":"steward://budgets/{budget.Id}/periods/{period.Id}/report"}}}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 200 )
            let doc = readResponseJson ctx
            let contents = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray() |> Seq.toList
            let text = contents.[0].GetProperty("text").GetString()
            Assert.True(text.Contains("periodId"))
            Assert.True(text.Contains("totals"))
            Assert.True(text.Contains("byCategory"))
        }

    [<Fact>]
    member _.``resources/read without auth returns unauthorized``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let ctx = createHttpContext factory
            setJsonBody ctx """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"steward://accounts"}}"""
            do! McpServer.mcpHandler ctx

            Assert.True( ctx.Response.StatusCode = 401 )
        }
