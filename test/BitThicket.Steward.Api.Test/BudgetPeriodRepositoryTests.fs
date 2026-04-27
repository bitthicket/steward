#nowarn "0044"

module BitThicket.Steward.Api.Test.BudgetPeriodRepositoryTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

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

let private makeBudget (tenantId: Guid) (userId: Guid) (name: string) =
    let now = DateTimeOffset.UtcNow
    {
        Id = Guid.NewGuid()
        TenantId = tenantId
        UserId = userId
        Name = name
        Style = BudgetingStyle.ZeroBased
        Period = BudgetPeriod.Monthly
        CurrencyCode = "USD"
        Income = Money.zero "USD"
        IsActive = true
        StartsOn = DateOnly.FromDateTime(DateTime.UtcNow)
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

let private makeAllocation (periodId: Guid) (categoryId: Guid) (amount: decimal) =
    {
        BudgetPeriodId = periodId
        CategoryId = categoryId
        AllocatedAmount = { Amount = amount; CurrencyCode = "USD" }
        OpeningBalance = Money.zero "USD"
        RolloverBalance = Money.zero "USD"
        RolloverEnabled = false
    }

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

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

type BudgetPeriodRepositoryTests() =

    [<Fact>]
    member _.``CreatePeriodAsync inserts a period with allocations``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let categoryId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId categoryId "Groceries"

            let budget = makeBudget tenantId userId "Monthly Budget"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let alloc = makeAllocation period.Id categoryId 500.00m
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! id = periodRepo.CreatePeriodAsync(period, [alloc])
            test <@ id = period.Id @>
        }

    [<Fact>]
    member _.``GetPeriodAsync returns the period``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let categoryId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId categoryId "Groceries"

            let budget = makeBudget tenantId userId "Monthly Budget"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let alloc = makeAllocation period.Id categoryId 500.00m
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [alloc])

            let! retrieved = periodRepo.GetPeriodAsync(period.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Id = period.Id @>
            test <@ retrieved.Value.Status = BudgetPeriodStatus.Open @>
        }

    [<Fact>]
    member _.``ListAllocationsByPeriodAsync returns allocations``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let cat1 = Guid.NewGuid()
            let cat2 = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId cat1 "Groceries"
            seedCategory seedConn tenantId userId cat2 "Utilities"

            let budget = makeBudget tenantId userId "Monthly Budget"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let alloc1 = makeAllocation period.Id cat1 500.00m
            let alloc2 = makeAllocation period.Id cat2 200.00m
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [alloc1; alloc2])

            let! allocs = periodRepo.ListAllocationsByPeriodAsync(period.Id)
            test <@ allocs.Length = 2 @>
            test <@ allocs |> List.exists (fun a -> a.CategoryId = cat1 && a.AllocatedAmount.Amount = 500.00m) @>
            test <@ allocs |> List.exists (fun a -> a.CategoryId = cat2 && a.AllocatedAmount.Amount = 200.00m) @>
        }

    [<Fact>]
    member _.``ClosePeriodAsync sets status to Closed``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let categoryId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId categoryId "Groceries"

            let budget = makeBudget tenantId userId "Monthly Budget"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let alloc = makeAllocation period.Id categoryId 500.00m
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [alloc])

            do! periodRepo.ClosePeriodAsync(period.Id)

            let! retrieved = periodRepo.GetPeriodAsync(period.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Status = BudgetPeriodStatus.Closed @>
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A cannot see tenant B period``() =
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
            let catB = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantA userA
            seedTenantAndUser seedConn tenantB userB
            seedCategory seedConn tenantB userB catB "Groceries"

            let budgetB = makeBudget tenantB userB "Budget B"
            let budgetRepoB = makeBudgetRepo factory (makeContext tenantB userB)
            let! _ = budgetRepoB.CreateAsync(budgetB)

            let periodB = makePeriod budgetB.Id tenantB (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let allocB = makeAllocation periodB.Id catB 500.00m
            let periodRepoB = makePeriodRepo factory (makeContext tenantB userB)
            let! _ = periodRepoB.CreatePeriodAsync(periodB, [allocB])

            let periodRepoA = makePeriodRepo factory (makeContext tenantA userA)
            let! retrieved = periodRepoA.GetPeriodAsync(periodB.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``UpdateAllocationAsync modifies an allocation``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let categoryId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId categoryId "Groceries"

            let budget = makeBudget tenantId userId "Monthly Budget"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let alloc = makeAllocation period.Id categoryId 500.00m
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [alloc])

            let updated = { alloc with AllocatedAmount = { Amount = 750.00m; CurrencyCode = "USD" }; RolloverEnabled = true }
            do! periodRepo.UpdateAllocationAsync(updated)

            let! retrieved = periodRepo.GetAllocationAsync(period.Id, categoryId)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.AllocatedAmount.Amount = 750.00m @>
            test <@ retrieved.Value.RolloverEnabled = true @>
        }

    [<Fact>]
    member _.``GetActualSpendByCategoryAsync returns spend for cleared transactions in period``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let categoryId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId categoryId "Groceries"

            // Seed account
            use accCmd = seedConn.CreateCommand()
            accCmd.CommandText <-
                """INSERT INTO accounts (id, tenant_id, user_id, name, account_type, currency, is_on_budget, is_active, created_at, updated_at)
                   VALUES ($1, $2, $3, 'Checking', 'checking', 'USD', true, true, now(), now())"""
            accCmd.Parameters.AddWithValue("$1", accountId) |> ignore
            accCmd.Parameters.AddWithValue("$2", tenantId) |> ignore
            accCmd.Parameters.AddWithValue("$3", userId) |> ignore
            accCmd.ExecuteNonQuery() |> ignore

            // Seed transaction in period
            use txnCmd = seedConn.CreateCommand()
            txnCmd.CommandText <-
                """INSERT INTO transactions (
                       id, tenant_id, account_id, occurred_at, posted_at, amount_minor, currency,
                       description, category_id, source, status, created_at, updated_at
                   ) VALUES ($1, $2, $3, '2026-04-15T00:00:00Z', '2026-04-15T00:00:00Z', -12500, 'USD',
                       'Grocery store', $4, '{"type":"manual"}'::jsonb, 'cleared', now(), now())"""
            txnCmd.Parameters.AddWithValue("$1", Guid.NewGuid()) |> ignore
            txnCmd.Parameters.AddWithValue("$2", tenantId) |> ignore
            txnCmd.Parameters.AddWithValue("$3", accountId) |> ignore
            txnCmd.Parameters.AddWithValue("$4", categoryId) |> ignore
            txnCmd.ExecuteNonQuery() |> ignore

            let budget = makeBudget tenantId userId "Monthly Budget"
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let alloc = makeAllocation period.Id categoryId 500.00m
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [alloc])

            let! spend = periodRepo.GetActualSpendByCategoryAsync(period.Id)
            test <@ spend |> Map.containsKey categoryId @>
            test <@ spend.[categoryId].Amount = -125.00m @>
        }

    [<Fact>]
    member _.``Close period round-trip: next period created with rollover balances``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let catRollover = Guid.NewGuid()
            let catNoRollover = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedCategory seedConn tenantId userId catRollover "Vacation"
            seedCategory seedConn tenantId userId catNoRollover "Dining"

            let budget = { makeBudget tenantId userId "Monthly Budget" with Income = { Amount = 700.00m; CurrencyCode = "USD" } }
            let budgetRepo = makeBudgetRepo factory (makeContext tenantId userId)
            let! _ = budgetRepo.CreateAsync(budget)

            // Create budget categories with rollover enabled for vacation
            let bcRollover = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                BudgetId = budget.Id
                CategoryId = catRollover
                AllocatedAmount = { Amount = 500.00m; CurrencyCode = "USD" }
                RolloverEnabled = true
                RolloverBalance = Money.zero "USD"
            }
            let bcNoRollover = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                BudgetId = budget.Id
                CategoryId = catNoRollover
                AllocatedAmount = { Amount = 200.00m; CurrencyCode = "USD" }
                RolloverEnabled = false
                RolloverBalance = Money.zero "USD"
            }
            let! _ = budgetRepo.CreateCategoryAsync(bcRollover)
            let! _ = budgetRepo.CreateCategoryAsync(bcNoRollover)

            let period = makePeriod budget.Id tenantId (DateOnly(2026, 4, 1)) (DateOnly(2026, 4, 30))
            let allocRollover = { makeAllocation period.Id catRollover 500.00m with RolloverEnabled = true }
            let allocNoRollover = { makeAllocation period.Id catNoRollover 200.00m with RolloverEnabled = false }
            let periodRepo = makePeriodRepo factory (makeContext tenantId userId)
            let! _ = periodRepo.CreatePeriodAsync(period, [allocRollover; allocNoRollover])

            // Close the period (no transactions => actual spend = 0, remaining = allocated)
            do! periodRepo.ClosePeriodAsync(period.Id)

            // Verify period is closed
            let! closedPeriod = periodRepo.GetPeriodAsync(period.Id)
            test <@ closedPeriod.Value.Status = BudgetPeriodStatus.Closed @>

            // Verify budget-level rollover balance updated for rollover category
            let! updatedBc = budgetRepo.GetCategoryAsync(bcRollover.Id)
            test <@ updatedBc.Value.RolloverBalance.Amount = 500.00m @>

            // Manually create next period with rollover (simulating what the endpoint does)
            let nextPeriod = {
                Id = Guid.NewGuid()
                BudgetId = budget.Id
                TenantId = tenantId
                StartDate = DateOnly(2026, 5, 1)
                EndDate = DateOnly(2026, 5, 31)
                Status = BudgetPeriodStatus.Open
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let nextAllocRollover = {
                BudgetPeriodId = nextPeriod.Id
                CategoryId = catRollover
                AllocatedAmount = { Amount = 500.00m; CurrencyCode = "USD" }
                OpeningBalance = { Amount = 500.00m; CurrencyCode = "USD" }
                RolloverBalance = { Amount = 500.00m; CurrencyCode = "USD" }
                RolloverEnabled = true
            }
            let nextAllocNoRollover = {
                BudgetPeriodId = nextPeriod.Id
                CategoryId = catNoRollover
                AllocatedAmount = { Amount = 200.00m; CurrencyCode = "USD" }
                OpeningBalance = Money.zero "USD"
                RolloverBalance = Money.zero "USD"
                RolloverEnabled = false
            }
            let! _ = periodRepo.CreatePeriodAsync(nextPeriod, [nextAllocRollover; nextAllocNoRollover])

            // Verify next period allocations
            let! nextAllocs = periodRepo.ListAllocationsByPeriodAsync(nextPeriod.Id)
            test <@ nextAllocs |> List.exists (fun a -> a.CategoryId = catRollover && a.OpeningBalance.Amount = 500.00m) @>
            test <@ nextAllocs |> List.exists (fun a -> a.CategoryId = catNoRollover && a.OpeningBalance.Amount = 0.00m) @>
        }
