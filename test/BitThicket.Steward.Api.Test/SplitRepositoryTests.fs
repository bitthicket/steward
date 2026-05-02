module BitThicket.Steward.Api.Test.SplitRepositoryTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

open BitThicket.Steward.Api.Test.TestHelpers

// ── Local seeding ──────────────────────────────────────────────────────────

let private seedSplit (conn: NpgsqlConnection) (tenantId: Guid) (txnId: Guid) (splitId: Guid) (amountMinor: int64) (currency: string) (sortOrder: int) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO transaction_splits (
               id, tenant_id, transaction_id, amount_minor, currency,
               category_id, description, memo, source, sort_order, created_at, updated_at
           ) VALUES ($1, $2, $3, $4, $5, NULL, 'Split', NULL, '{"type":"manual"}'::jsonb, $6, now(), now())"""
    cmd.Parameters.AddWithValue("$1", splitId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", txnId) |> ignore
    cmd.Parameters.AddWithValue("$4", amountMinor) |> ignore
    cmd.Parameters.AddWithValue("$5", currency) |> ignore
    cmd.Parameters.AddWithValue("$6", sortOrder) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Tests ──────────────────────────────────────────────────────────────────

type SplitRepositoryTests() =

    [<Fact>]
    member _.``Create and get split``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            let splitId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let repo = SplitRepository.create factory accessor

            let split: TransactionSplit = {
                Id = splitId
                TenantId = tenantId
                TransactionId = txnId
                Amount = { Amount = -50.00m; CurrencyCode = "USD" }
                CategoryId = None
                Description = Some "Coffee"
                Memo = None
                Source = SplitSource.Manual
                SortOrder = 0
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! createdId = repo.CreateAsync(split)
            test <@ createdId = splitId @>

            let! fetchedOpt = repo.GetAsync(splitId)
            test <@ fetchedOpt.IsSome @>
            let fetched = fetchedOpt.Value
            test <@ fetched.Amount.Amount = -50.00m @>
            test <@ fetched.Description = Some "Coffee" @>
        }

    [<Fact>]
    member _.``List splits by transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow
            seedSplit seedConn tenantId txnId (Guid.NewGuid()) -3000L "USD" 0
            seedSplit seedConn tenantId txnId (Guid.NewGuid()) -7000L "USD" 1

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let repo = SplitRepository.create factory accessor

            let! splits = repo.ListByTransactionAsync(txnId)
            test <@ splits.Length = 2 @>
            test <@ splits.[0].Amount.Amount = -30.00m @>
            test <@ splits.[1].Amount.Amount = -70.00m @>
        }

    [<Fact>]
    member _.``Update split``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            let splitId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow
            seedSplit seedConn tenantId txnId splitId -5000L "USD" 0

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let repo = SplitRepository.create factory accessor

            let! originalOpt = repo.GetAsync(splitId)
            let original = originalOpt.Value
            let updated = { original with Amount = { Amount = -25.00m; CurrencyCode = "USD" }; Description = Some "Updated" }
            do! repo.UpdateAsync(updated)

            let! fetchedOpt = repo.GetAsync(splitId)
            test <@ fetchedOpt.Value.Amount.Amount = -25.00m @>
            test <@ fetchedOpt.Value.Description = Some "Updated" @>
        }

    [<Fact>]
    member _.``Delete split``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            let splitId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow
            seedSplit seedConn tenantId txnId splitId -5000L "USD" 0

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let repo = SplitRepository.create factory accessor

            do! repo.DeleteAsync(splitId)
            let! fetchedOpt = repo.GetAsync(splitId)
            test <@ fetchedOpt.IsNone @>
        }

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot access tenant B split``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let tenantA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            let splitId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantA userId accountId "Checking" "USD"
            seedTransaction seedConn tenantA accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow
            seedSplit seedConn tenantA txnId splitId -5000L "USD" 0

            let accessorA =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantA; UserId = userId } }
            let accessorB =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantB; UserId = userId } }

            let repoB = SplitRepository.create factory accessorB
            let! fetchedOpt = repoB.GetAsync(splitId)
            test <@ fetchedOpt.IsNone @>
        }
