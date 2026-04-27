#nowarn "0044"

module BitThicket.Steward.Api.Test.BudgetRepositoryTests

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

let private makeBudgetCategory (tenantId: Guid) (budgetId: Guid) (categoryId: Guid) (allocated: decimal) =
    {
        Id = Guid.NewGuid()
        TenantId = tenantId
        BudgetId = budgetId
        CategoryId = categoryId
        AllocatedAmount = { Amount = allocated; CurrencyCode = "USD" }
        RolloverEnabled = false
        RolloverBalance = Money.zero "USD"
    }

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

let private makeRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    BudgetRepository.create factory accessor

type BudgetRepositoryTests() =

    [<Fact>]
    member _.``CreateAsync inserts a budget and returns its id``() =
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

            let budget = makeBudget tenantId userId "Groceries Budget"
            let repo = makeRepo factory (makeContext tenantId userId)
            let! id = repo.CreateAsync(budget)
            test <@ id = budget.Id @>
        }

    [<Fact>]
    member _.``GetAsync returns the budget for the current tenant``() =
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

            let budget = makeBudget tenantId userId "Groceries Budget"
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(budget)
            let! retrieved = repo.GetAsync(budget.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Id = budget.Id @>
            test <@ retrieved.Value.Name = budget.Name @>
            test <@ retrieved.Value.Style = budget.Style @>
            test <@ retrieved.Value.Period = budget.Period @>
            test <@ retrieved.Value.CurrencyCode = budget.CurrencyCode @>
            test <@ retrieved.Value.IsActive = budget.IsActive @>
        }

    [<Fact>]
    member _.``ListAsync returns only budgets for the current tenant``() =
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
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantA userA
            seedTenantAndUser seedConn tenantB userB

            let budgetA1 = makeBudget tenantA userA "Budget A1"
            let budgetA2 = makeBudget tenantA userA "Budget A2"
            let budgetB = makeBudget tenantB userB "Budget B"

            let repoA = makeRepo factory (makeContext tenantA userA)
            let repoB = makeRepo factory (makeContext tenantB userB)

            let! _ = repoA.CreateAsync(budgetA1)
            let! _ = repoA.CreateAsync(budgetA2)
            let! _ = repoB.CreateAsync(budgetB)

            let! listA = repoA.ListAsync()
            let! listB = repoB.ListAsync()

            test <@ listA.Length = 2 @>
            test <@ listB.Length = 1 @>
            test <@ listA |> List.exists (fun b -> b.Id = budgetA1.Id) @>
            test <@ listA |> List.exists (fun b -> b.Id = budgetA2.Id) @>
            test <@ listB |> List.exists (fun b -> b.Id = budgetB.Id) @>
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A cannot see tenant B budget``() =
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
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantA userA
            seedTenantAndUser seedConn tenantB userB

            let budgetB = makeBudget tenantB userB "Budget B"
            let repoB = makeRepo factory (makeContext tenantB userB)
            let! _ = repoB.CreateAsync(budgetB)

            let repoA = makeRepo factory (makeContext tenantA userA)
            let! retrieved = repoA.GetAsync(budgetB.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``UpdateAsync modifies an existing budget``() =
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

            let budget = makeBudget tenantId userId "Original Name"
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(budget)

            let updated = { budget with Name = "Updated Name"; IsActive = false }
            do! repo.UpdateAsync(updated)

            let! retrieved = repo.GetAsync(budget.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Name = "Updated Name" @>
            test <@ retrieved.Value.IsActive = false @>
        }

    [<Fact>]
    member _.``DeleteAsync removes a budget``() =
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

            let budget = makeBudget tenantId userId "To Delete"
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(budget)
            do! repo.DeleteAsync(budget.Id)

            let! retrieved = repo.GetAsync(budget.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``BudgetCategory round-trips correctly``() =
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
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(budget)

            let bc = makeBudgetCategory tenantId budget.Id categoryId 500.00m
            let! bcId = repo.CreateCategoryAsync(bc)
            test <@ bcId = bc.Id @>

            let! retrieved = repo.GetCategoryAsync(bc.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.AllocatedAmount.Amount = 500.00m @>
            test <@ retrieved.Value.AllocatedAmount.CurrencyCode = "USD" @>
            test <@ retrieved.Value.RolloverEnabled = false @>
            test <@ retrieved.Value.RolloverBalance.Amount = 0.00m @>
        }

    [<Fact>]
    member _.``ListCategoriesByBudgetAsync returns only categories for the given budget``() =
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
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(budget)

            let bc1 = makeBudgetCategory tenantId budget.Id cat1 500.00m
            let bc2 = makeBudgetCategory tenantId budget.Id cat2 200.00m
            let! _ = repo.CreateCategoryAsync(bc1)
            let! _ = repo.CreateCategoryAsync(bc2)

            let! list = repo.ListCategoriesByBudgetAsync(budget.Id)
            test <@ list.Length = 2 @>
        }

    [<Fact>]
    member _.``UpdateCategoryAsync modifies an existing budget category``() =
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
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(budget)

            let bc = makeBudgetCategory tenantId budget.Id categoryId 500.00m
            let! _ = repo.CreateCategoryAsync(bc)

            let updated = { bc with AllocatedAmount = { Amount = 750.00m; CurrencyCode = "USD" }; RolloverEnabled = true }
            do! repo.UpdateCategoryAsync(updated)

            let! retrieved = repo.GetCategoryAsync(bc.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.AllocatedAmount.Amount = 750.00m @>
            test <@ retrieved.Value.RolloverEnabled = true @>
        }
