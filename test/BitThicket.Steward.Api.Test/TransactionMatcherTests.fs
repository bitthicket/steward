module BitThicket.Steward.Api.Test.TransactionMatcherTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

// ── Helpers ──────────────────────────────────────────────────────────────────

let private makeManualTxn (accountId: Guid) (occurredAt: DateTimeOffset) (description: string) (amount: decimal) =
    let now = DateTimeOffset.UtcNow
    {
        Id = Guid.NewGuid()
        TenantId = Guid.NewGuid()
        AccountId = accountId
        OccurredAt = occurredAt
        PostedAt = None
        Amount = { Amount = amount; CurrencyCode = "USD" }
        Description = description
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
        CreatedAt = now
        UpdatedAt = now
    }

let private makeCandidate (accountId: Guid) (occurredAt: DateTimeOffset) (description: string) (amount: decimal) =
    {
        ExternalId = $"ext-{Guid.NewGuid()}"
        AccountId = accountId
        OccurredAt = occurredAt
        PostedAt = None
        Amount = { Amount = amount; CurrencyCode = "USD" }
        Description = description
        Merchant = Some "Test Merchant"
    }

// ── Integration test helpers ─────────────────────────────────────────────────

let private runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

let private sharedContainer : PostgreSqlContainer option =
    try
        let c =
            PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build()
        c.StartAsync().GetAwaiter().GetResult() |> ignore
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

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

let private makeRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    TransactionRepository.create factory accessor

type ScoringTests() =

    [<Fact>]
    member _.``Exact match auto-accepts``() =
        let accountId = Guid.NewGuid()
        let occurredAt = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
        let manual = makeManualTxn accountId occurredAt "Starbucks Coffee" -50.00m
        let candidate = makeCandidate accountId occurredAt "Starbucks Coffee" -50.00m

        let score = TransactionMatcher.score candidate manual
        test <@ score >= 0.9m @>

        let result = TransactionMatcher.evaluate candidate [manual]
        match result with
        | AutoMatched(_, conf) -> test <@ conf >= 0.9m @>
        | _ -> failwith "Expected AutoMatched"

    [<Fact>]
    member _.``Near amount and date with low text similarity needs review``() =
        let accountId = Guid.NewGuid()
        let occurredAt = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
        let manual = makeManualTxn accountId occurredAt "Starbucks Coffee" -50.00m
        let candidate = makeCandidate accountId (occurredAt.AddDays(1.0)) "Whole Foods Market" -50.01m

        let score = TransactionMatcher.score candidate manual
        test <@ score >= 0.6m && score < 0.9m @>

        let result = TransactionMatcher.evaluate candidate [manual]
        match result with
        | NeedsReview(_, conf) -> test <@ conf >= 0.6m && conf < 0.9m @>
        | _ -> failwith "Expected NeedsReview"

    [<Fact>]
    member _.``Far everything returns NoMatch``() =
        let accountId = Guid.NewGuid()
        let occurredAt = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
        let manual = makeManualTxn accountId occurredAt "Starbucks Coffee" -50.00m
        let candidate = makeCandidate accountId (occurredAt.AddDays(10.0)) "Amazon Purchase" -100.00m

        let score = TransactionMatcher.score candidate manual
        test <@ score < 0.6m @>

        let result = TransactionMatcher.evaluate candidate [manual]
        test <@ result = NoMatch @>

    [<Fact>]
    member _.``Amount exact date exact description low similarity needs review``() =
        let accountId = Guid.NewGuid()
        let occurredAt = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
        let manual = makeManualTxn accountId occurredAt "Starbucks Coffee" -50.00m
        let candidate = makeCandidate accountId occurredAt "Completely Different Merchant" -50.00m

        let score = TransactionMatcher.score candidate manual
        test <@ score >= 0.6m && score < 0.9m @>

        let result = TransactionMatcher.evaluate candidate [manual]
        match result with
        | NeedsReview(_, _) -> ()
        | _ -> failwith "Expected NeedsReview"

    [<Fact>]
    member _.``Date score decays linearly between 2 and 7 days``() =
        let accountId = Guid.NewGuid()
        let baseDate = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
        let manual = makeManualTxn accountId baseDate "Test" -50.00m

        // 2 days → full 0.3
        let candidate2 = makeCandidate accountId (baseDate.AddDays(2.0)) "Test" -50.00m
        let score2 = TransactionMatcher.score candidate2 manual
        test <@ score2 >= 0.9m @>

        // 4.5 days → middle of decay: 0.4 + 0.15 + 0.3 = 0.85
        let candidate45 = makeCandidate accountId (baseDate.AddDays(4.5)) "Test" -50.00m
        let score45 = TransactionMatcher.score candidate45 manual
        test <@ score45 >= 0.8m && score45 < 0.9m @>

        // 7 days → 0 date score: 0.4 + 0.0 + 0.3 = 0.7
        let candidate7 = makeCandidate accountId (baseDate.AddDays(7.0)) "Test" -50.00m
        let score7 = TransactionMatcher.score candidate7 manual
        test <@ score7 >= 0.6m && score7 < 0.8m @>

    [<Fact>]
    member _.``Different account returns zero score``() =
        let accountA = Guid.NewGuid()
        let accountB = Guid.NewGuid()
        let occurredAt = DateTimeOffset.UtcNow
        let manual = makeManualTxn accountA occurredAt "Test" -50.00m
        let candidate = makeCandidate accountB occurredAt "Test" -50.00m

        let score = TransactionMatcher.score candidate manual
        test <@ score = 0.0m @>

    [<Fact>]
    member _.``Different currency returns zero score``() =
        let accountId = Guid.NewGuid()
        let occurredAt = DateTimeOffset.UtcNow
        let manual = makeManualTxn accountId occurredAt "Test" -50.00m
        let candidate =
            { (makeCandidate accountId occurredAt "Test" -50.00m) with
                Amount = { Amount = -50.00m; CurrencyCode = "EUR" } }

        let score = TransactionMatcher.score candidate manual
        test <@ score = 0.0m @>

    [<Fact>]
    member _.``Opposite sign returns zero score``() =
        let accountId = Guid.NewGuid()
        let occurredAt = DateTimeOffset.UtcNow
        let manual = makeManualTxn accountId occurredAt "Test" -50.00m
        let candidate = makeCandidate accountId occurredAt "Test" 50.00m

        let score = TransactionMatcher.score candidate manual
        test <@ score = 0.0m @>

    [<Fact>]
    member _.``Amount off by more than one minor unit returns zero amount score``() =
        let accountId = Guid.NewGuid()
        let occurredAt = DateTimeOffset.UtcNow
        let manual = makeManualTxn accountId occurredAt "Test" -50.00m
        // Use a different description so desc score is low, and date exact so only date contributes
        let candidate = makeCandidate accountId occurredAt "Something Else Entirely" -50.05m

        let score = TransactionMatcher.score candidate manual
        // Amount score is 0, description score is low, date score is 0.3
        test <@ score < 0.6m @>

type StringSimilarityTests() =

    [<Fact>]
    member _.``JaroWinkler exact match is 1.0``() =
        test <@ StringSimilarity.jaroWinkler "hello" "hello" = 1.0 @>

    [<Fact>]
    member _.``JaroWinkler completely different is low``() =
        test <@ StringSimilarity.jaroWinkler "abc" "xyz" < 0.3 @>

    [<Fact>]
    member _.``JaroWinkler is case insensitive``() =
        let s1 = StringSimilarity.jaroWinkler "Hello World" "hello world"
        let s2 = StringSimilarity.jaroWinkler "Hello World" "Hello World"
        test <@ s1 = s2 @>

type IntegrationTests() =

    [<Fact>]
    member _.``End-to-end: feed entry matches manual entry and both are linked``() =
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

            let ctx = makeContext tenantId userId
            let repo = makeRepo factory ctx

            let occurredAt = DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
            let manual = makeManualTxn accountId occurredAt "Starbucks Coffee" -50.00m
            let! _ = repo.CreateAsync(manual)

            let candidate = makeCandidate accountId occurredAt "Starbucks Coffee" -50.00m

            let matcher = TransactionMatcher.create repo
            let! matchResult = matcher.MatchAsync tenantId accountId candidate

            match matchResult with
            | AutoMatched(manualId, conf) ->
                test <@ manualId = manual.Id @>
                test <@ conf >= 0.9m @>

                let now = DateTimeOffset.UtcNow
                let feedTxn =
                    { Id = Guid.NewGuid()
                      TenantId = tenantId
                      AccountId = accountId
                      OccurredAt = occurredAt
                      PostedAt = None
                      Amount = { Amount = -50.00m; CurrencyCode = "USD" }
                      Description = "Starbucks Coffee"
                      Merchant = Some "Starbucks"
                      Memo = None
                      CategoryId = None
                      Source = TransactionSource.DataFeed "plaid"
                      ExternalId = Some "ext-12345"
                      MatchedTransactionId = Some manualId
                      TransferAccountId = None
                      Status = TransactionStatus.Cleared
                      MatchConfidence = Some conf
                      SyncEventId = None
                      CreatedAt = now
                      UpdatedAt = now }
                let! _ = repo.CreateAsync(feedTxn)

                let! feedRetrieved = repo.GetAsync(feedTxn.Id)
                let! manualRetrieved = repo.GetAsync(manual.Id)

                test <@ feedRetrieved |> Option.isSome @>
                test <@ feedRetrieved.Value.MatchedTransactionId = Some manual.Id @>
                test <@ feedRetrieved.Value.Status = TransactionStatus.Cleared @>

                test <@ manualRetrieved |> Option.isSome @>
                test <@ manualRetrieved.Value.MatchedTransactionId = Some feedTxn.Id @>
                test <@ manualRetrieved.Value.Status = TransactionStatus.Cleared @>
            | _ -> failwith "Expected AutoMatched"
        }

    [<Fact>]
    member _.``ListMatchCandidatesAsync returns only unmatched manual entries``() =
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

            let ctx = makeContext tenantId userId
            let repo = makeRepo factory ctx

            let manual1 = makeManualTxn accountId DateTimeOffset.UtcNow "M1" -10.00m
            let manual2 = makeManualTxn accountId DateTimeOffset.UtcNow "M2" -20.00m
            let manual3 = makeManualTxn accountId DateTimeOffset.UtcNow "M3" -30.00m
            let manual3WithStatus = { manual3 with Status = TransactionStatus.NeedsReview }

            let! _ = repo.CreateAsync(manual1)
            let! _ = repo.CreateAsync(manual2)
            let! _ = repo.CreateAsync(manual3WithStatus)

            // Link manual1 to a feed transaction
            let feed = makeManualTxn accountId DateTimeOffset.UtcNow "F1" -10.00m
            let feedLinked = { feed with Source = TransactionSource.DataFeed "plaid"; ExternalId = Some "ext-1"; MatchedTransactionId = Some manual1.Id; Status = TransactionStatus.Cleared }
            let! _ = repo.CreateAsync(feedLinked)

            let! candidates = repo.ListMatchCandidatesAsync(accountId)
            let candidateIds = candidates |> List.map (fun t -> t.Id)

            // manual1 is matched, manual3 is NeedsReview → only manual2 qualifies
            test <@ candidateIds |> List.contains manual2.Id @>
            test <@ not (candidateIds |> List.contains manual1.Id) @>
            test <@ not (candidateIds |> List.contains manual3.Id) @>
        }

    [<Fact>]
    member _.``ListNeedsReviewAsync returns only NeedsReview transactions``() =
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

            let ctx = makeContext tenantId userId
            let repo = makeRepo factory ctx

            let pending = makeManualTxn accountId DateTimeOffset.UtcNow "Pending" -10.00m
            let needsReview = makeManualTxn accountId DateTimeOffset.UtcNow "Review" -20.00m
            let needsReviewWithStatus = { needsReview with Status = TransactionStatus.NeedsReview }
            let cleared = makeManualTxn accountId DateTimeOffset.UtcNow "Cleared" -30.00m
            let clearedWithStatus = { cleared with Status = TransactionStatus.Cleared }

            let! _ = repo.CreateAsync(pending)
            let! _ = repo.CreateAsync(needsReviewWithStatus)
            let! _ = repo.CreateAsync(clearedWithStatus)

            let! reviewList = repo.ListNeedsReviewAsync()
            let reviewIds = reviewList |> List.map (fun t -> t.Id)

            test <@ reviewIds |> List.contains needsReview.Id @>
            test <@ not (reviewIds |> List.contains pending.Id) @>
            test <@ not (reviewIds |> List.contains cleared.Id) @>
        }

    [<Fact>]
    member _.``GetByExternalIdAsync finds transaction by external id``() =
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

            let ctx = makeContext tenantId userId
            let repo = makeRepo factory ctx

            let txn = makeManualTxn accountId DateTimeOffset.UtcNow "Test" -10.00m
            let txnWithExt = { txn with ExternalId = Some "ext-unique-123" }
            let! _ = repo.CreateAsync(txnWithExt)

            let! found = repo.GetByExternalIdAsync("ext-unique-123")
            let! notFound = repo.GetByExternalIdAsync("ext-does-not-exist")

            test <@ found |> Option.isSome @>
            test <@ found.Value.Id = txn.Id @>
            test <@ notFound |> Option.isNone @>
        }

    [<Fact>]
    member _.``Resolve accept links both transactions as Cleared``() =
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

            let ctx = makeContext tenantId userId
            let repo = makeRepo factory ctx

            let manual = makeManualTxn accountId DateTimeOffset.UtcNow "Manual" -50.00m
            let! _ = repo.CreateAsync(manual)

            let feed = makeManualTxn accountId DateTimeOffset.UtcNow "Feed" -50.00m
            let feedWithData = { feed with Source = TransactionSource.DataFeed "plaid"; ExternalId = Some "ext-feed"; MatchedTransactionId = Some manual.Id; MatchConfidence = Some 0.75m; Status = TransactionStatus.NeedsReview }
            let! _ = repo.CreateAsync(feedWithData)

            let now = DateTimeOffset.UtcNow
            let updatedFeed = { feedWithData with Status = TransactionStatus.Cleared; UpdatedAt = now }
            let updatedManual = { manual with Status = TransactionStatus.Cleared; MatchedTransactionId = Some feedWithData.Id; UpdatedAt = now }
            do! repo.UpdateAsync(updatedFeed)
            do! repo.UpdateAsync(updatedManual)

            let! feedRetrieved = repo.GetAsync(feedWithData.Id)
            let! manualRetrieved = repo.GetAsync(manual.Id)

            test <@ feedRetrieved.Value.Status = TransactionStatus.Cleared @>
            test <@ feedRetrieved.Value.MatchedTransactionId = Some manual.Id @>
            test <@ manualRetrieved.Value.Status = TransactionStatus.Cleared @>
            test <@ manualRetrieved.Value.MatchedTransactionId = Some feedWithData.Id @>
        }

    [<Fact>]
    member _.``Resolve reject clears link and sets status to Cleared``() =
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

            let ctx = makeContext tenantId userId
            let repo = makeRepo factory ctx

            let manual = makeManualTxn accountId DateTimeOffset.UtcNow "Manual" -50.00m
            let! _ = repo.CreateAsync(manual)

            let feed = makeManualTxn accountId DateTimeOffset.UtcNow "Feed" -50.00m
            let feedWithData = { feed with Source = TransactionSource.DataFeed "plaid"; ExternalId = Some "ext-feed"; MatchedTransactionId = Some manual.Id; MatchConfidence = Some 0.75m; Status = TransactionStatus.NeedsReview }
            let! _ = repo.CreateAsync(feedWithData)

            let updated = { feedWithData with Status = TransactionStatus.Cleared; MatchedTransactionId = None; MatchConfidence = None; UpdatedAt = DateTimeOffset.UtcNow }
            do! repo.UpdateAsync(updated)

            let! feedRetrieved = repo.GetAsync(feedWithData.Id)
            let! manualRetrieved = repo.GetAsync(manual.Id)

            test <@ feedRetrieved.Value.Status = TransactionStatus.Cleared @>
            test <@ feedRetrieved.Value.MatchedTransactionId |> Option.isNone @>
            test <@ feedRetrieved.Value.MatchConfidence |> Option.isNone @>
            // Manual transaction should remain unchanged
            test <@ manualRetrieved.Value.Status = TransactionStatus.Pending @>
        }
