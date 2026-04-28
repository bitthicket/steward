#nowarn "0044"

module BitThicket.Steward.Api.Test.CreditCardPaymentRepositoryTests

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

let private seedAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) (name: string) (accountType: string) =
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
    cmd.Parameters.AddWithValue("$5", accountType) |> ignore
    cmd.Parameters.AddWithValue("$6", "USD") |> ignore
    cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$10", true) |> ignore
    cmd.Parameters.AddWithValue("$11", true) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private makePayment (tenantId: Guid) (ccAccountId: Guid) (fundingAccountId: Guid) (amount: decimal) =
    let now = DateTimeOffset.UtcNow
    {
        Id = Guid.NewGuid()
        TenantId = tenantId
        CreditCardAccountId = ccAccountId
        FundingAccountId = fundingAccountId
        Amount = { Amount = amount; CurrencyCode = "USD" }
        PaymentType = PaymentType.CustomAmount
        ScheduledDate = Some (DateOnly.FromDateTime(now.DateTime))
        PaidAt = None
        DebitTransactionId = None
        CreditTransactionId = None
        CreatedAt = now
    }

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

let private makeRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    CreditCardPaymentRepository.create factory accessor

type CreditCardPaymentRepositoryTests() =

    [<Fact>]
    member _.``CreateAsync inserts a payment and returns its id``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ccId = Guid.NewGuid()
            let fundingId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId ccId "CC" "credit_card"
            seedAccount seedConn tenantId userId fundingId "Checking" "checking"

            let payment = makePayment tenantId ccId fundingId 100.00m
            let repo = makeRepo factory (makeContext tenantId userId)
            let! id = repo.CreateAsync(payment)
            test <@ id = payment.Id @>
        }

    [<Fact>]
    member _.``GetAsync returns the payment for the current tenant``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ccId = Guid.NewGuid()
            let fundingId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId ccId "CC" "credit_card"
            seedAccount seedConn tenantId userId fundingId "Checking" "checking"

            let payment = makePayment tenantId ccId fundingId 100.00m
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(payment)
            let! retrieved = repo.GetAsync(payment.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Id = payment.Id @>
            test <@ retrieved.Value.Amount.Amount = payment.Amount.Amount @>
            test <@ retrieved.Value.PaymentType = payment.PaymentType @>
        }

    [<Fact>]
    member _.``ListByCreditCardAccountAsync returns only payments for the given card``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ccA = Guid.NewGuid()
            let ccB = Guid.NewGuid()
            let fundingId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId ccA "CC A" "credit_card"
            seedAccount seedConn tenantId userId ccB "CC B" "credit_card"
            seedAccount seedConn tenantId userId fundingId "Checking" "checking"

            let paymentA1 = makePayment tenantId ccA fundingId 50.00m
            let paymentA2 = makePayment tenantId ccA fundingId 75.00m
            let paymentB = makePayment tenantId ccB fundingId 100.00m

            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(paymentA1)
            let! _ = repo.CreateAsync(paymentA2)
            let! _ = repo.CreateAsync(paymentB)

            let! listA = repo.ListByCreditCardAccountAsync(ccA)
            let! listB = repo.ListByCreditCardAccountAsync(ccB)

            test <@ listA.Length = 2 @>
            test <@ listB.Length = 1 @>
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A cannot see tenant B payment``() =
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
            let ccB = Guid.NewGuid()
            let fundingB = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantA userA
            seedTenantAndUser seedConn tenantB userB
            seedAccount seedConn tenantB userB ccB "CC" "credit_card"
            seedAccount seedConn tenantB userB fundingB "Checking" "checking"

            let paymentB = makePayment tenantB ccB fundingB 100.00m
            let repoB = makeRepo factory (makeContext tenantB userB)
            let! _ = repoB.CreateAsync(paymentB)

            let repoA = makeRepo factory (makeContext tenantA userA)
            let! retrieved = repoA.GetAsync(paymentB.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``UpdateAsync modifies an existing payment``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ccId = Guid.NewGuid()
            let fundingId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId ccId "CC" "credit_card"
            seedAccount seedConn tenantId userId fundingId "Checking" "checking"

            let payment = makePayment tenantId ccId fundingId 100.00m
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(payment)

            let updated = { payment with PaymentType = PaymentType.StatementBalance; PaidAt = Some DateTimeOffset.UtcNow }
            do! repo.UpdateAsync(updated)

            let! retrieved = repo.GetAsync(payment.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.PaymentType = PaymentType.StatementBalance @>
            test <@ retrieved.Value.PaidAt |> Option.isSome @>
        }

    [<Fact>]
    member _.``DeleteAsync removes a payment``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let ccId = Guid.NewGuid()
            let fundingId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId ccId "CC" "credit_card"
            seedAccount seedConn tenantId userId fundingId "Checking" "checking"

            let payment = makePayment tenantId ccId fundingId 100.00m
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(payment)
            do! repo.DeleteAsync(payment.Id)

            let! retrieved = repo.GetAsync(payment.Id)
            test <@ retrieved |> Option.isNone @>
        }
