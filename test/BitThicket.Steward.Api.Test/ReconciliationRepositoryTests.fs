module BitThicket.Steward.Api.Test.ReconciliationRepositoryTests

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

let private seedAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) (currency: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO accounts (id, tenant_id, user_id, name, account_type, currency,
               institution_name, external_id, credit_card_info, is_on_budget, is_active,
               deleted_at, created_at, updated_at)
           VALUES ($1, $2, $3, 'Test Account', 'checking', $4,
               NULL, NULL, NULL, true, true, NULL, now(), now())"""
    cmd.Parameters.AddWithValue("$1", accountId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", currency) |> ignore
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
               NULL, '{"type":"manual"}', NULL, NULL, NULL,
               $7, NULL, NULL, now(), now())"""
    cmd.Parameters.AddWithValue("$1", txnId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", accountId) |> ignore
    cmd.Parameters.AddWithValue("$4", postedAt.UtcDateTime) |> ignore
    cmd.Parameters.AddWithValue("$5", postedAt.UtcDateTime) |> ignore
    cmd.Parameters.AddWithValue("$6", amountMinor) |> ignore
    cmd.Parameters.AddWithValue("$7", status) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

let private makeRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    ReconciliationRepository.create factory accessor

let private makeTxnRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    TransactionRepository.create factory accessor

type ReconciliationRepositoryTests() =

    [<Fact>]
    member _.``CreateAsync inserts a reconciliation and returns its id``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let recon = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                AccountId = accountId
                StatementBalance = { Amount = 1000m; CurrencyCode = "USD" }
                StatementDate = DateOnly(2026, 4, 15)
                Status = ReconciliationStatus.Open
                Note = None
                CreatedByUserId = userId
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = None
            }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! id = repo.CreateAsync(recon)
            test <@ id = recon.Id @>
        }

    [<Fact>]
    member _.``GetAsync returns the reconciliation for the current tenant``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let recon = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                AccountId = accountId
                StatementBalance = { Amount = 1000m; CurrencyCode = "USD" }
                StatementDate = DateOnly(2026, 4, 15)
                Status = ReconciliationStatus.Open
                Note = None
                CreatedByUserId = userId
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = None
            }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(recon)
            let! retrieved = repo.GetAsync(recon.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Id = recon.Id @>
            test <@ retrieved.Value.Status = ReconciliationStatus.Open @>
        }

    [<Fact>]
    member _.``ListCandidateTransactionsAsync returns cleared transactions on or before statement date not yet linked to a completed reconciliation``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let txn1 = Guid.NewGuid()
            let txn2 = Guid.NewGuid()
            let txn3 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"
            seedTransaction seedConn tenantId accountId txn2 200L (DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero)) "cleared"
            seedTransaction seedConn tenantId accountId txn3 300L (DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)) "cleared"

            let repo = makeRepo factory (makeContext tenantId userId)
            let! candidates = repo.ListCandidateTransactionsAsync(accountId, DateOnly(2026, 4, 15))

            test <@ candidates.Length = 2 @>
            test <@ candidates |> List.exists (fun t -> t.Id = txn1) @>
            test <@ candidates |> List.exists (fun t -> t.Id = txn2) @>
            test <@ candidates |> List.forall (fun t -> t.Id <> txn3) @>
        }

    [<Fact>]
    member _.``ListCandidateTransactionsAsync excludes transactions already linked to a completed reconciliation``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let txn1 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"

            // Create a completed reconciliation that links txn1
            let prevReconId = Guid.NewGuid()
            use cmd = seedConn.CreateCommand()
            cmd.CommandText <-
                """INSERT INTO reconciliations (id, tenant_id, account_id, statement_date, statement_balance_minor,
                       currency, status, note, created_by_user_id, started_at, completed_at)
                   VALUES ($1, $2, $3, '2026-04-10', 100, 'USD', 'completed', NULL, $4, now(), now())"""
            cmd.Parameters.AddWithValue("$1", prevReconId) |> ignore
            cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
            cmd.Parameters.AddWithValue("$3", accountId) |> ignore
            cmd.Parameters.AddWithValue("$4", userId) |> ignore
            cmd.ExecuteNonQuery() |> ignore

            use cmd2 = seedConn.CreateCommand()
            cmd2.CommandText <- "INSERT INTO reconciliation_transactions (reconciliation_id, transaction_id) VALUES ($1, $2)"
            cmd2.Parameters.AddWithValue("$1", prevReconId) |> ignore
            cmd2.Parameters.AddWithValue("$2", txn1) |> ignore
            cmd2.ExecuteNonQuery() |> ignore

            let repo = makeRepo factory (makeContext tenantId userId)
            let! candidates = repo.ListCandidateTransactionsAsync(accountId, DateOnly(2026, 4, 15))

            test <@ candidates |> List.forall (fun t -> t.Id <> txn1) @>
        }

    [<Fact>]
    member _.``UpdateIncludedTransactionsAsync adds and removes links``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let recon = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                AccountId = accountId
                StatementBalance = { Amount = 1000m; CurrencyCode = "USD" }
                StatementDate = DateOnly(2026, 4, 15)
                Status = ReconciliationStatus.Open
                Note = None
                CreatedByUserId = userId
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = None
            }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(recon)

            let txn1 = Guid.NewGuid()
            let txn2 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"
            seedTransaction seedConn tenantId accountId txn2 200L (DateTimeOffset(2026, 4, 11, 0, 0, 0, TimeSpan.Zero)) "cleared"

            // Include both
            do! repo.UpdateIncludedTransactionsAsync(recon.Id, [txn1; txn2], [])
            let! afterAddOpt = repo.GetWithTransactionsAsync(recon.Id)
            let (_, txnsAfterAdd) = afterAddOpt |> Option.get
            test <@ txnsAfterAdd.Length = 2 @>

            // Exclude txn1
            do! repo.UpdateIncludedTransactionsAsync(recon.Id, [], [txn1])
            let! afterRemoveOpt = repo.GetWithTransactionsAsync(recon.Id)
            let (_, txnsAfterRemove) = afterRemoveOpt |> Option.get
            test <@ txnsAfterRemove.Length = 1 @>
            test <@ txnsAfterRemove.[0].Id = txn2 @>
        }

    [<Fact>]
    member _.``CompleteAsync succeeds when balance matches and marks transactions reconciled``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let recon = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                AccountId = accountId
                StatementBalance = { Amount = 3.00m; CurrencyCode = "USD" }
                StatementDate = DateOnly(2026, 4, 15)
                Status = ReconciliationStatus.Open
                Note = None
                CreatedByUserId = userId
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = None
            }
            let repo = makeRepo factory (makeContext tenantId userId)
            let txnRepo = makeTxnRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(recon)

            let txn1 = Guid.NewGuid()
            let txn2 = Guid.NewGuid()
            let txn3 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"
            seedTransaction seedConn tenantId accountId txn2 100L (DateTimeOffset(2026, 4, 11, 0, 0, 0, TimeSpan.Zero)) "cleared"
            seedTransaction seedConn tenantId accountId txn3 100L (DateTimeOffset(2026, 4, 12, 0, 0, 0, TimeSpan.Zero)) "cleared"

            do! repo.UpdateIncludedTransactionsAsync(recon.Id, [txn1; txn2; txn3], [])
            let! result = repo.CompleteAsync(recon.Id, false, None)

            test <@ result = Ok 0L @>

            let! updatedRecon = repo.GetAsync(recon.Id)
            test <@ updatedRecon.Value.Status = ReconciliationStatus.Completed @>

            let! txn1After = txnRepo.GetAsync(txn1)
            let! txn2After = txnRepo.GetAsync(txn2)
            let! txn3After = txnRepo.GetAsync(txn3)
            test <@ txn1After.Value.Status = TransactionStatus.Reconciled @>
            test <@ txn2After.Value.Status = TransactionStatus.Reconciled @>
            test <@ txn3After.Value.Status = TransactionStatus.Reconciled @>
        }

    [<Fact>]
    member _.``CompleteAsync returns error when balance mismatches and force is false``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let recon = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                AccountId = accountId
                StatementBalance = { Amount = 5.00m; CurrencyCode = "USD" }
                StatementDate = DateOnly(2026, 4, 15)
                Status = ReconciliationStatus.Open
                Note = None
                CreatedByUserId = userId
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = None
            }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(recon)

            let txn1 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"
            do! repo.UpdateIncludedTransactionsAsync(recon.Id, [txn1], [])

            let! result = repo.CompleteAsync(recon.Id, false, None)
            test <@ result = Error "diff:-400" @>

            let! updatedRecon = repo.GetAsync(recon.Id)
            test <@ updatedRecon.Value.Status = ReconciliationStatus.Open @>
        }

    [<Fact>]
    member _.``CompleteAsync succeeds with force=true and stamps a note``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let recon = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                AccountId = accountId
                StatementBalance = { Amount = 5.00m; CurrencyCode = "USD" }
                StatementDate = DateOnly(2026, 4, 15)
                Status = ReconciliationStatus.Open
                Note = None
                CreatedByUserId = userId
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = None
            }
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(recon)

            let txn1 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"
            do! repo.UpdateIncludedTransactionsAsync(recon.Id, [txn1], [])

            let! result = repo.CompleteAsync(recon.Id, true, Some "User override")
            test <@ result = Ok -400L @>

            let! updatedRecon = repo.GetAsync(recon.Id)
            test <@ updatedRecon.Value.Status = ReconciliationStatus.Completed @>
            test <@ updatedRecon.Value.Note |> Option.isSome @>
            test <@ updatedRecon.Value.Note.Value.Contains("discrepancy") @>
        }

    [<Fact>]
    member _.``AbortAsync marks reconciliation aborted without changing transaction statuses``() =
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
            seedAccount seedConn tenantId userId accountId "USD"

            let recon = {
                Id = Guid.NewGuid()
                TenantId = tenantId
                AccountId = accountId
                StatementBalance = { Amount = 1.00m; CurrencyCode = "USD" }
                StatementDate = DateOnly(2026, 4, 15)
                Status = ReconciliationStatus.Open
                Note = None
                CreatedByUserId = userId
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = None
            }
            let repo = makeRepo factory (makeContext tenantId userId)
            let txnRepo = makeTxnRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(recon)

            let txn1 = Guid.NewGuid()
            seedTransaction seedConn tenantId accountId txn1 100L (DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero)) "cleared"
            do! repo.UpdateIncludedTransactionsAsync(recon.Id, [txn1], [])

            do! repo.AbortAsync(recon.Id)

            let! updatedRecon = repo.GetAsync(recon.Id)
            test <@ updatedRecon.Value.Status = ReconciliationStatus.Aborted @>
            test <@ updatedRecon.Value.CompletedAt |> Option.isSome @>

            let! txn1After = txnRepo.GetAsync(txn1)
            test <@ txn1After.Value.Status = TransactionStatus.Cleared @>
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A cannot see tenant B reconciliation``() =
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
            seedAccount seedConn tenantB userB accountB "USD"

            let reconB = {
                Id = Guid.NewGuid()
                TenantId = tenantB
                AccountId = accountB
                StatementBalance = { Amount = 1000m; CurrencyCode = "USD" }
                StatementDate = DateOnly(2026, 4, 15)
                Status = ReconciliationStatus.Open
                Note = None
                CreatedByUserId = userB
                StartedAt = DateTimeOffset.UtcNow
                CompletedAt = None
            }
            let repoB = makeRepo factory (makeContext tenantB userB)
            let! _ = repoB.CreateAsync(reconB)

            let repoA = makeRepo factory (makeContext tenantA userA)
            let! retrieved = repoA.GetAsync(reconB.Id)
            test <@ retrieved |> Option.isNone @>
        }
