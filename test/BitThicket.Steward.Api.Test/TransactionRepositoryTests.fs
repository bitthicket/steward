#nowarn "0044"

module BitThicket.Steward.Api.Test.TransactionRepositoryTests

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

let private seedAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) (name: string) =
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
    cmd.Parameters.AddWithValue("$6", "USD") |> ignore
    cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$10", true) |> ignore
    cmd.Parameters.AddWithValue("$11", true) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private makeTransaction (tenantId: Guid) (accountId: Guid) (amount: decimal) =
    let now = DateTimeOffset.UtcNow
    {
        Id = Guid.NewGuid()
        TenantId = tenantId
        AccountId = accountId
        OccurredAt = now
        PostedAt = None
        Amount = { Amount = amount; CurrencyCode = "USD" }
        Description = "Test transaction"
        Merchant = Some "Test Merchant"
        Memo = None
        CategoryId = None
        Source = TransactionSource.Manual
        ExternalId = None
        MatchedTransactionId = None
        TransferAccountId = None
        Status = TransactionStatus.Pending
        MatchConfidence = None
        SyncEventId = None
        DeletedAt = None
        CreatedAt = now
        UpdatedAt = now
    }

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

let private makeRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    TransactionRepository.create factory accessor

type TransactionRepositoryTests() =

    [<Fact>]
    member _.``CreateAsync inserts a transaction and returns its id``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            let txn = makeTransaction tenantId accountId -50.00m
            let repo = makeRepo factory (makeContext tenantId userId)
            let! id = repo.CreateAsync(txn)
            test <@ id = txn.Id @>
        }

    [<Fact>]
    member _.``GetAsync returns the transaction for the current tenant``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            let txn = makeTransaction tenantId accountId -50.00m
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(txn)
            let! retrieved = repo.GetAsync(txn.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Id = txn.Id @>
            test <@ retrieved.Value.Description = txn.Description @>
            test <@ retrieved.Value.Amount.Amount = txn.Amount.Amount @>
            test <@ retrieved.Value.Amount.CurrencyCode = txn.Amount.CurrencyCode @>
            test <@ retrieved.Value.Merchant = txn.Merchant @>
            test <@ retrieved.Value.Source = txn.Source @>
            test <@ retrieved.Value.Status = txn.Status @>
        }

    [<Fact>]
    member _.``GetAsync returns None for non-existent transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            let repo = makeRepo factory (makeContext tenantId userId)
            let! retrieved = repo.GetAsync(Guid.NewGuid())
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``ListByAccountAsync returns only transactions for the given account``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountA = Guid.NewGuid()
            let accountB = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountA "Checking A"
            seedAccount seedConn tenantId userId accountB "Checking B"

            let txnA1 = makeTransaction tenantId accountA -10.00m
            let txnA2 = makeTransaction tenantId accountA -20.00m
            let txnB = makeTransaction tenantId accountB -30.00m

            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(txnA1)
            let! _ = repo.CreateAsync(txnA2)
            let! _ = repo.CreateAsync(txnB)

            let! listA = repo.ListByAccountAsync(accountA)
            let! listB = repo.ListByAccountAsync(accountB)

            test <@ listA.Length = 2 @>
            test <@ listB.Length = 1 @>
            test <@ listA |> List.exists (fun t -> t.Id = txnA1.Id) @>
            test <@ listA |> List.exists (fun t -> t.Id = txnA2.Id) @>
            test <@ listB |> List.exists (fun t -> t.Id = txnB.Id) @>
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A cannot see tenant B transaction``() =
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
            let accountB = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantA userA
            seedTenantAndUser seedConn tenantB userB
            seedAccount seedConn tenantB userB accountB "Checking B"

            let txnB = makeTransaction tenantB accountB -50.00m
            let repoB = makeRepo factory (makeContext tenantB userB)
            let! _ = repoB.CreateAsync(txnB)

            let repoA = makeRepo factory (makeContext tenantA userA)
            let! retrieved = repoA.GetAsync(txnB.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``UpdateAsync modifies an existing transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            let txn = makeTransaction tenantId accountId -50.00m
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(txn)

            let updated = { txn with Description = "Updated description"; Status = TransactionStatus.Cleared }
            do! repo.UpdateAsync(updated)

            let! retrieved = repo.GetAsync(txn.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Description = "Updated description" @>
            test <@ retrieved.Value.Status = TransactionStatus.Cleared @>
        }

    [<Fact>]
    member _.``DeleteAsync removes a transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            let txn = makeTransaction tenantId accountId -50.00m
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(txn)
            do! repo.DeleteAsync(txn.Id)

            let! retrieved = repo.GetAsync(txn.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``Money round-trips correctly for USD``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            let txn = { (makeTransaction tenantId accountId -123.45m) with Amount = { Amount = -123.45m; CurrencyCode = "USD" } }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(txn)
            let! retrieved = repo.GetAsync(txn.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Amount.Amount = -123.45m @>
            test <@ retrieved.Value.Amount.CurrencyCode = "USD" @>
        }

    [<Fact>]
    member _.``TransactionSource round-trips for DataFeed``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            let txn = { (makeTransaction tenantId accountId -50.00m) with Source = TransactionSource.DataFeed "plaid" }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(txn)
            let! retrieved = repo.GetAsync(txn.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Source = TransactionSource.DataFeed "plaid" @>
        }

    [<Fact>]
    member _.``Split sum-to-parent invariant rejects violating inserts``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            // Insert a transaction for $100.00 (10000 minor USD)
            let txn = { (makeTransaction tenantId accountId 100.00m) with Amount = { Amount = 100.00m; CurrencyCode = "USD" } }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(txn)

            // Attempt to insert splits that sum to $60.00 (should fail)
            use! conn = factory.OpenForTenantAsync({ TenantId = tenantId; UserId = userId })
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO transaction_splits (id, tenant_id, transaction_id, amount_minor, currency, source, sort_order, created_at, updated_at)
                   VALUES ($1, $2, $3, $4, $5, $6, $7, now(), now())"""
            cmd.Parameters.AddWithValue("$1", Guid.NewGuid()) |> ignore
            cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", txn.Id) |> ignore
            cmd.Parameters.AddWithValue("$4", 6000L) |> ignore  // $60.00
            cmd.Parameters.AddWithValue("$5", "USD") |> ignore
            let sourceParam = cmd.CreateParameter()
            sourceParam.ParameterName <- "$6"
            sourceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
            sourceParam.Value <- box """{"type":"manual"}"""
            cmd.Parameters.Add(sourceParam) |> ignore
            cmd.Parameters.AddWithValue("$7", 0) |> ignore

            let! ex = Assert.ThrowsAsync<PostgresException>(fun () -> cmd.ExecuteNonQueryAsync() :> Task)
            test <@ ex.SqlState = "P0001" @>  // RAISException / trigger failure
        }

    [<Fact>]
    member _.``TransactionSource round-trips for Import``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId
            seedAccount seedConn tenantId userId accountId "Checking"

            let txn = { (makeTransaction tenantId accountId -50.00m) with Source = TransactionSource.Import "csv" }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(txn)
            let! retrieved = repo.GetAsync(txn.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Source = TransactionSource.Import "csv" @>
        }
