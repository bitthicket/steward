#nowarn "0044"

module BitThicket.Steward.Api.Test.AccountRepositoryTests

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

/// Seed a tenant, user, and membership directly (as the postgres superuser,
/// which bypasses RLS) so we have stable fixture data for repo tests.
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

let private makeAccount (tenantId: Guid) (userId: Guid) (accountType: AccountType) =
    let now = DateTimeOffset.UtcNow
    {
        Id = Guid.NewGuid()
        TenantId = tenantId
        UserId = userId
        Name = $"Test {accountType.ToString()}"
        AccountType = accountType
        CurrencyCode = "USD"
        InstitutionName = Some "Test Bank"
        ExternalId = Some "ext-123"
        CreditCardInfo = None
        IsOnBudget = AccountRepository.defaultIsOnBudget accountType
        IsActive = true
        CreatedAt = now
        UpdatedAt = now
    }

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

let private makeRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    AccountRepository.create factory accessor

type AccountRepositoryTests() =

    [<Fact>]
    member _.``CreateAsync inserts an account and returns its id``() =
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

            let account = makeAccount tenantId userId AccountType.Checking
            let repo = makeRepo factory (makeContext tenantId userId)
            let! id = repo.CreateAsync(account)
            test <@ id = account.Id @>
        }

    [<Fact>]
    member _.``GetAsync returns the account for the current tenant``() =
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

            let account = makeAccount tenantId userId AccountType.Checking
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(account)
            let! retrieved = repo.GetAsync(account.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Id = account.Id @>
            test <@ retrieved.Value.Name = account.Name @>
            test <@ retrieved.Value.AccountType = account.AccountType @>
            test <@ retrieved.Value.CurrencyCode = account.CurrencyCode @>
        }

    [<Fact>]
    member _.``GetAsync returns None for non-existent account``() =
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

            let repo = makeRepo factory (makeContext tenantId userId)
            let! retrieved = repo.GetAsync(Guid.NewGuid())
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``ListAsync returns only accounts for the current tenant``() =
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

            let accountA1 = makeAccount tenantA userA AccountType.Checking
            let accountA2 = makeAccount tenantA userA AccountType.Savings
            let accountB = makeAccount tenantB userB AccountType.CreditCard

            let repoA = makeRepo factory (makeContext tenantA userA)
            let repoB = makeRepo factory (makeContext tenantB userB)

            let! _ = repoA.CreateAsync(accountA1)
            let! _ = repoA.CreateAsync(accountA2)
            let! _ = repoB.CreateAsync(accountB)

            let! listA = repoA.ListAsync()
            let! listB = repoB.ListAsync()

            test <@ listA.Length = 2 @>
            test <@ listB.Length = 1 @>
            test <@ listA |> List.exists (fun a -> a.Id = accountA1.Id) @>
            test <@ listA |> List.exists (fun a -> a.Id = accountA2.Id) @>
            test <@ listB |> List.exists (fun a -> a.Id = accountB.Id) @>
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A cannot see tenant B account``() =
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

            let accountB = makeAccount tenantB userB AccountType.Investment
            let repoB = makeRepo factory (makeContext tenantB userB)
            let! _ = repoB.CreateAsync(accountB)

            let repoA = makeRepo factory (makeContext tenantA userA)
            let! retrieved = repoA.GetAsync(accountB.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``UpdateAsync modifies an existing account``() =
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

            let account = makeAccount tenantId userId AccountType.Checking
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(account)

            let updated = { account with Name = "Updated Name"; IsOnBudget = false }
            do! repo.UpdateAsync(updated)

            let! retrieved = repo.GetAsync(account.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Name = "Updated Name" @>
            test <@ retrieved.Value.IsOnBudget = false @>
        }

    [<Fact>]
    member _.``DeleteAsync removes an account``() =
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

            let account = makeAccount tenantId userId AccountType.Cash
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(account)
            do! repo.DeleteAsync(account.Id)

            let! retrieved = repo.GetAsync(account.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``is_on_budget default per account_type matches ADR-009``() =
        test <@ AccountRepository.defaultIsOnBudget AccountType.Checking = true @>
        test <@ AccountRepository.defaultIsOnBudget AccountType.Savings = true @>
        test <@ AccountRepository.defaultIsOnBudget AccountType.CreditCard = true @>
        test <@ AccountRepository.defaultIsOnBudget AccountType.Cash = true @>
        test <@ AccountRepository.defaultIsOnBudget AccountType.Investment = false @>
        test <@ AccountRepository.defaultIsOnBudget AccountType.Loan = false @>

    [<Fact>]
    member _.``credit_card_info round-trips correctly``() =
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

            let ccInfo = {
                CreditLimit = { Amount = 10000m; CurrencyCode = "USD" }
                StatementBalance = Some { Amount = 2500m; CurrencyCode = "USD" }
                MinimumPayment = Some { Amount = 50m; CurrencyCode = "USD" }
                DueDate = Some (DateOnly(2025, 6, 15))
                Apr = Some 0.1999m
            }

            let account =
                { (makeAccount tenantId userId AccountType.CreditCard) with
                    CreditCardInfo = Some ccInfo }

            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(account)
            let! retrieved = repo.GetAsync(account.Id)

            test <@ retrieved |> Option.isSome @>
            let actual = retrieved.Value.CreditCardInfo.Value
            test <@ actual.CreditLimit.Amount = ccInfo.CreditLimit.Amount @>
            test <@ actual.CreditLimit.CurrencyCode = ccInfo.CreditLimit.CurrencyCode @>
            test <@ actual.StatementBalance.Value.Amount = ccInfo.StatementBalance.Value.Amount @>
            test <@ actual.MinimumPayment.Value.Amount = ccInfo.MinimumPayment.Value.Amount @>
            test <@ actual.DueDate.Value = ccInfo.DueDate.Value @>
            test <@ actual.Apr.Value = ccInfo.Apr.Value @>
        }

    [<Fact>]
    member _.``credit_card_info round-trips as None when absent``() =
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

            let account = makeAccount tenantId userId AccountType.Checking
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(account)
            let! retrieved = repo.GetAsync(account.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.CreditCardInfo = None @>
        }
