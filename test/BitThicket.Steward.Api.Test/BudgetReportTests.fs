#nowarn "0044"

module BitThicket.Steward.Api.Test.BudgetReportTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain
open BitThicket.Steward.Pricing

let private runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

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

let private seedTenantAndUser (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) =
    use cmd = conn.CreateCommand()
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

let private seedCategory (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (categoryId: Guid) (name: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO categories (id, tenant_id, user_id, name, is_system, created_at)
           VALUES ($1, $2, $3, $4, false, now())"""
    cmd.Parameters.AddWithValue("$1", categoryId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", name) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) (currency: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO accounts (
               id, tenant_id, user_id, name, account_type, currency,
               institution_name, external_id, credit_card_info,
               is_on_budget, is_active, created_at, updated_at
           ) VALUES ($1, $2, $3, 'Checking', 'checking', $4, $5, $5, $5, true, true, now(), now())"""
    cmd.Parameters.AddWithValue("$1", accountId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", currency) |> ignore
    cmd.Parameters.AddWithValue("$5", DBNull.Value) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedTransaction
    (conn: NpgsqlConnection)
    (tenantId: Guid)
    (accountId: Guid)
    (categoryId: Guid option)
    (amountMinor: int64)
    (currency: string)
    (status: string)
    (occurredAt: DateTimeOffset)
    =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO transactions (
               id, tenant_id, account_id, occurred_at, posted_at, amount_minor, currency,
               description, category_id, source, status, created_at, updated_at
           ) VALUES ($1, $2, $3, $4, $4, $5, $6, 'Test', $7, '{"type":"manual"}'::jsonb, $8, now(), now())"""
    cmd.Parameters.AddWithValue("$1", Guid.NewGuid()) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", accountId) |> ignore
    cmd.Parameters.AddWithValue("$4", occurredAt.UtcDateTime) |> ignore
    cmd.Parameters.AddWithValue("$5", amountMinor) |> ignore
    cmd.Parameters.AddWithValue("$6", currency) |> ignore
    match categoryId with
    | Some cid -> cmd.Parameters.AddWithValue("$7", cid) |> ignore
    | None -> cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$8", status) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

let private makeBudget (tenantId: Guid) (userId: Guid) (name: string) (currency: string) =
    let now = DateTimeOffset.UtcNow
    {
        Id = Guid.NewGuid()
        TenantId = tenantId
        UserId = userId
        Name = name
        Style = BudgetingStyle.Flexible
        Period = BudgetPeriod.Monthly
        CurrencyCode = currency
        Income = { Amount = 2000.00m; CurrencyCode = currency }
        IsActive = true
        StartsOn = DateOnly(2026, 4, 1)
        CreatedAt = now
        UpdatedAt = now
    }

let private makePeriod (budgetId: Guid) (tenantId: Guid) (startDate: DateOnly) (endDate: DateOnly) =
    let now = DateTimeOffset.UtcNow
    {
        Id = Guid.NewGuid()
        BudgetId = budgetId
        TenantId = tenantId
        StartDate = startDate
        EndDate = endDate
        Status = BudgetPeriodStatus.Open
        CreatedAt = now
        UpdatedAt = now
    }

let private makeAllocation (periodId: Guid) (categoryId: Guid) (amount: decimal) (currency: string) =
    {
        BudgetPeriodId = periodId
        CategoryId = categoryId
        AllocatedAmount = { Amount = amount; CurrencyCode = currency }
        OpeningBalance = Money.zero currency
        RolloverBalance = Money.zero currency
        RolloverEnabled = false
    }

let private makeBudgetRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    BudgetRepository.create factory accessor

let private makePeriodRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    BudgetPeriodRepository.create factory accessor

let private makeCategoryRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    CategoryRepository.create factory accessor

/// Mock price provider that returns a fixed rate for any pair.
/// For tests we only need EUR→USD at 1.10.
type MockPriceProvider(rate: decimal) =
    interface IPriceProvider with
        member _.GetSpotAsync(baseCurr, quoteCurr) =
            Task.FromResult {
                Base = baseCurr
                Quote = quoteCurr
                AsOf = DateTimeOffset.UtcNow
                Value = rate
                Source = "mock"
                FetchedAt = DateTimeOffset.UtcNow
            }
        member _.GetHistoricalAsync(baseCurr, quoteCurr, asOf) =
            Task.FromResult None

let private createReportHttpContext (factory: IDbConnectionFactory) (tenantContext: TenantContext) (priceProvider: IPriceProvider) =
    let services = ServiceCollection()
    services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
    services.AddSingleton<IPriceProvider>(priceProvider) |> ignore

    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some tenantContext }
    services.AddSingleton<ITenantContextAccessor>(accessor) |> ignore

    let budgetAccessor =
        { new ITenantContextAccessor with
            member _.Context = Some tenantContext }
    services.AddSingleton<IBudgetRepository>(BudgetRepository.create factory budgetAccessor) |> ignore

    let periodAccessor =
        { new ITenantContextAccessor with
            member _.Context = Some tenantContext }
    services.AddSingleton<IBudgetPeriodRepository>(BudgetPeriodRepository.create factory periodAccessor) |> ignore

    let catAccessor =
        { new ITenantContextAccessor with
            member _.Context = Some tenantContext }
    services.AddSingleton<ICategoryRepository>(CategoryRepository.create factory catAccessor) |> ignore

    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Response.Body <- new MemoryStream()
    ctx

let private readResponseJson (ctx: HttpContext) =
    ctx.Response.Body.Position <- 0L
    use reader = new StreamReader(ctx.Response.Body)
    let json = reader.ReadToEnd()
    JsonDocument.Parse(json)

type BudgetReportTests() =

    [<Fact>]
    member _.``Report matches expected totals for 10 transactions across 3 categories``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let catGroceries = Guid.NewGuid()
            let catDining = Guid.NewGuid()
            let catUtilities = Guid.NewGuid()
            let accountId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId catGroceries "Groceries"
            seedCategory seedConn tenantId userId catDining "Dining"
            seedCategory seedConn tenantId userId catUtilities "Utilities"
            seedAccount seedConn tenantId userId accountId "USD"

            // Seed 10 transactions across 3 categories in April 2026
            // Groceries: 5 transactions totalling -320.00
            seedTransaction seedConn tenantId accountId (Some catGroceries) -4500L  "USD" "cleared"      (DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero))
            seedTransaction seedConn tenantId accountId (Some catGroceries) -6200L  "USD" "cleared"      (DateTimeOffset(2026, 4, 8, 0, 0, 0, TimeSpan.Zero))
            seedTransaction seedConn tenantId accountId (Some catGroceries) -8300L  "USD" "reconciled"   (DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero))
            seedTransaction seedConn tenantId accountId (Some catGroceries) -5500L  "USD" "cleared"      (DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero))
            seedTransaction seedConn tenantId accountId (Some catGroceries) -7500L  "USD" "cleared"      (DateTimeOffset(2026, 4, 22, 0, 0, 0, TimeSpan.Zero))
            // Dining: 3 transactions totalling -145.00
            seedTransaction seedConn tenantId accountId (Some catDining)    -3500L  "USD" "cleared"      (DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero))
            seedTransaction seedConn tenantId accountId (Some catDining)    -6000L  "USD" "cleared"      (DateTimeOffset(2026, 4, 12, 0, 0, 0, TimeSpan.Zero))
            seedTransaction seedConn tenantId accountId (Some catDining)    -5000L  "USD" "reconciled"   (DateTimeOffset(2026, 4, 19, 0, 0, 0, TimeSpan.Zero))
            // Utilities: 2 transactions totalling -180.00
            seedTransaction seedConn tenantId accountId (Some catUtilities) -12000L "USD" "cleared"      (DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero))
            seedTransaction seedConn tenantId accountId (Some catUtilities) -6000L  "USD" "cleared"      (DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero))

            let budget = makeBudget tenantId userId "Monthly Budget" "USD"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let allocG = makeAllocation period.Id catGroceries 500.00m "USD"
            let allocD = makeAllocation period.Id catDining 200.00m "USD"
            let allocU = makeAllocation period.Id catUtilities 200.00m "USD"
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [allocG; allocD; allocU])

            let ctx = createReportHttpContext factory (makeContext tenantId userId) (MockPriceProvider(1.0m))
            do! BudgetEndpoints.getReportHandler budget.Id period.Id ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx

            // Totals
            Assert.Equal(90000L, doc.RootElement.GetProperty("totals").GetProperty("allocatedMinor").GetInt64())
            Assert.Equal(64500L, doc.RootElement.GetProperty("totals").GetProperty("spentMinor").GetInt64())
            Assert.Equal(25500L, doc.RootElement.GetProperty("totals").GetProperty("remainingMinor").GetInt64())

            let byCategory = doc.RootElement.GetProperty("byCategory").EnumerateArray() |> Seq.toList
            Assert.Equal(3, byCategory.Length)

            let groceries = byCategory |> List.find (fun c -> c.GetProperty("name").GetString() = "Groceries")
            Assert.Equal(50000L, groceries.GetProperty("allocatedMinor").GetInt64())
            Assert.Equal(32000L, groceries.GetProperty("spentMinor").GetInt64())
            Assert.Equal(18000L, groceries.GetProperty("remainingMinor").GetInt64())
            Assert.Equal(64.0m, groceries.GetProperty("percentUsed").GetDecimal())

            let dining = byCategory |> List.find (fun c -> c.GetProperty("name").GetString() = "Dining")
            Assert.Equal(20000L, dining.GetProperty("allocatedMinor").GetInt64())
            Assert.Equal(14500L, dining.GetProperty("spentMinor").GetInt64())
            Assert.Equal(5500L, dining.GetProperty("remainingMinor").GetInt64())
            Assert.Equal(72.5m, dining.GetProperty("percentUsed").GetDecimal())

            let utilities = byCategory |> List.find (fun c -> c.GetProperty("name").GetString() = "Utilities")
            Assert.Equal(20000L, utilities.GetProperty("allocatedMinor").GetInt64())
            Assert.Equal(18000L, utilities.GetProperty("spentMinor").GetInt64())
            Assert.Equal(2000L, utilities.GetProperty("remainingMinor").GetInt64())
            Assert.Equal(90.0m, utilities.GetProperty("percentUsed").GetDecimal())
        }

    [<Fact>]
    member _.``Foreign-currency transaction converts at report-time rate``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let catTravel = Guid.NewGuid()
            let accountEur = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId catTravel "Travel"
            seedAccount seedConn tenantId userId accountEur "EUR"

            // Seed a €100.00 transaction (10000 minor EUR)
            seedTransaction seedConn tenantId accountEur (Some catTravel) -10000L "EUR" "cleared" (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero))

            let budget = makeBudget tenantId userId "Travel Budget" "USD"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let alloc = makeAllocation period.Id catTravel 500.00m "USD"
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [alloc])

            // Mock rate: 1 EUR = 1.10 USD
            let ctx = createReportHttpContext factory (makeContext tenantId userId) (MockPriceProvider(1.10m))
            do! BudgetEndpoints.getReportHandler budget.Id period.Id ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx

            let byCategory = doc.RootElement.GetProperty("byCategory").EnumerateArray() |> Seq.toList
            Assert.Equal(1, byCategory.Length)
            let travel = byCategory.[0]

            // €100.00 * 1.10 = $110.00 → 11000 minor
            Assert.Equal(11000L, travel.GetProperty("spentMinor").GetInt64())
            Assert.Equal(39000L, travel.GetProperty("remainingMinor").GetInt64())  // 50000 - 11000
        }

    [<Fact>]
    member _.``Pending and NeedsReview transactions are excluded from report``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let catFood = Guid.NewGuid()
            let accountId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId catFood "Food"
            seedAccount seedConn tenantId userId accountId "USD"

            // Cleared transaction: -50.00
            seedTransaction seedConn tenantId accountId (Some catFood) -5000L "USD" "cleared"     (DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero))
            // Pending transaction: -30.00 (should be excluded)
            seedTransaction seedConn tenantId accountId (Some catFood) -3000L "USD" "pending"     (DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero))
            // NeedsReview transaction: -20.00 (should be excluded)
            seedTransaction seedConn tenantId accountId (Some catFood) -2000L "USD" "needs_review" (DateTimeOffset(2026, 4, 7, 0, 0, 0, TimeSpan.Zero))
            // Reconciled transaction: -10.00
            seedTransaction seedConn tenantId accountId (Some catFood) -1000L "USD" "reconciled"  (DateTimeOffset(2026, 4, 8, 0, 0, 0, TimeSpan.Zero))

            let budget = makeBudget tenantId userId "Food Budget" "USD"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let alloc = makeAllocation period.Id catFood 200.00m "USD"
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [alloc])

            let ctx = createReportHttpContext factory (makeContext tenantId userId) (MockPriceProvider(1.0m))
            do! BudgetEndpoints.getReportHandler budget.Id period.Id ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx

            let byCategory = doc.RootElement.GetProperty("byCategory").EnumerateArray() |> Seq.toList
            Assert.Equal(1, byCategory.Length)
            let food = byCategory.[0]

            // Only cleared (-50) + reconciled (-10) = -60 → spentMinor = 6000
            Assert.Equal(6000L, food.GetProperty("spentMinor").GetInt64())
            Assert.Equal(14000L, food.GetProperty("remainingMinor").GetInt64())
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A report does not include tenant B transactions``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantA = Guid.NewGuid()
            let userA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()
            let userB = Guid.NewGuid()
            let catA = Guid.NewGuid()
            let catB = Guid.NewGuid()
            let accountA = Guid.NewGuid()
            let accountB = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantA userA
            seedTenantAndUser seedConn tenantB userB
            seedCategory seedConn tenantA userA catA "Groceries"
            seedCategory seedConn tenantB userB catB "Groceries"
            seedAccount seedConn tenantA userA accountA "USD"
            seedAccount seedConn tenantB userB accountB "USD"

            // Tenant A: -75.00
            seedTransaction seedConn tenantA accountA (Some catA) -7500L "USD" "cleared" (DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero))
            // Tenant B: -150.00
            seedTransaction seedConn tenantB accountB (Some catB) -15000L "USD" "cleared" (DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero))

            let budgetA = makeBudget tenantA userA "Budget A" "USD"
            let budgetB = makeBudget tenantB userB "Budget B" "USD"
            let budgetRepoA = makeBudgetRepo factory (makeContext tenantA userA)
            let budgetRepoB = makeBudgetRepo factory (makeContext tenantB userB)
            let! _ = budgetRepoA.CreateAsync(budgetA)
            let! _ = budgetRepoB.CreateAsync(budgetB)

            let periodA = makePeriod budgetA.Id tenantA (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let periodB = makePeriod budgetB.Id tenantB (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let allocA = makeAllocation periodA.Id catA 300.00m "USD"
            let allocB = makeAllocation periodB.Id catB 300.00m "USD"
            let periodRepoA = makePeriodRepo factory (makeContext tenantA userA)
            let periodRepoB = makePeriodRepo factory (makeContext tenantB userB)
            let! _ = periodRepoA.CreatePeriodAsync(periodA, [allocA])
            let! _ = periodRepoB.CreatePeriodAsync(periodB, [allocB])

            // Request report for tenant A
            let ctxA = createReportHttpContext factory (makeContext tenantA userA) (MockPriceProvider(1.0m))
            do! BudgetEndpoints.getReportHandler budgetA.Id periodA.Id ctxA

            Assert.Equal(200, ctxA.Response.StatusCode)
            let docA = readResponseJson ctxA
            let byCategoryA = docA.RootElement.GetProperty("byCategory").EnumerateArray() |> Seq.toList
            Assert.Equal(1, byCategoryA.Length)
            Assert.Equal(7500L, byCategoryA.[0].GetProperty("spentMinor").GetInt64())

            // Request report for tenant B
            let ctxB = createReportHttpContext factory (makeContext tenantB userB) (MockPriceProvider(1.0m))
            do! BudgetEndpoints.getReportHandler budgetB.Id periodB.Id ctxB

            Assert.Equal(200, ctxB.Response.StatusCode)
            let docB = readResponseJson ctxB
            let byCategoryB = docB.RootElement.GetProperty("byCategory").EnumerateArray() |> Seq.toList
            Assert.Equal(1, byCategoryB.Length)
            Assert.Equal(15000L, byCategoryB.[0].GetProperty("spentMinor").GetInt64())
        }

    [<Fact>]
    member _.``Current report alias returns 404 when no open period exists``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId

            let budget = makeBudget tenantId userId "No Period Budget" "USD"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let ctx = createReportHttpContext factory (makeContext tenantId userId) (MockPriceProvider(1.0m))
            do! BudgetEndpoints.getCurrentReportHandler budget.Id ctx

            test <@ ctx.Response.StatusCode = 404 @>
        }
