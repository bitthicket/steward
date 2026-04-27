module BitThicket.Steward.Api.Test.SplitEndpointsTests

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
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

open BitThicket.Steward.Api.Test.TestHelpers

// ── Tests ──────────────────────────────────────────────────────────────────

type SplitEndpointsTests() =

    [<Fact>]
    member _.``POST /api/transactions/{id}/splits creates a split``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "splitcreate@example.com"
            let jwtDoc =
                let ctx = createHttpContextWithAuth factory token
                setJsonBody ctx """{"email":"x","password":"x","displayName":"x","tenantDisplayName":"x"}"""
                Auth.registerHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
                readResponseJson ctx
            let tenantId = Guid.Parse(jwtDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(jwtDoc.RootElement.GetProperty("userId").GetString())

            use seedConn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx $"""{{"amountMinor":-5000,"currency":"USD","description":"Coffee","sortOrder":0}}"""
            do! SplitEndpoints.createSplitHandler txnId createCtx

            test <@ createCtx.Response.StatusCode = 201 @>
            let doc = readResponseJson createCtx
            test <@ doc.RootElement.GetProperty("amount").GetDecimal() = -50.00m @>
            test <@ doc.RootElement.GetProperty("description").GetString() = "Coffee" @>
        }

    [<Fact>]
    member _.``POST /api/transactions/{id}/splits returns 404 for missing transaction``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "split404@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"amountMinor":-5000,"currency":"USD","sortOrder":0}"""
            do! SplitEndpoints.createSplitHandler (Guid.NewGuid()) createCtx

            test <@ createCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``POST /api/transactions/{id}/splits validates currency mismatch``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "splitcurr@example.com"
            let jwtDoc =
                let ctx = createHttpContextWithAuth factory token
                setJsonBody ctx """{"email":"x","password":"x","displayName":"x","tenantDisplayName":"x"}"""
                Auth.registerHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
                readResponseJson ctx
            let tenantId = Guid.Parse(jwtDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(jwtDoc.RootElement.GetProperty("userId").GetString())

            use seedConn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"amountMinor":-5000,"currency":"EUR","sortOrder":0}"""
            do! SplitEndpoints.createSplitHandler txnId createCtx

            test <@ createCtx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``GET /api/transactions/{id}/splits lists splits``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "splitlist@example.com"
            let jwtDoc =
                let ctx = createHttpContextWithAuth factory token
                setJsonBody ctx """{"email":"x","password":"x","displayName":"x","tenantDisplayName":"x"}"""
                Auth.registerHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
                readResponseJson ctx
            let tenantId = Guid.Parse(jwtDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(jwtDoc.RootElement.GetProperty("userId").GetString())

            use seedConn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow

            let accessor = { new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let splitRepo = SplitRepository.create factory accessor
            let split: TransactionSplit = {
                Id = Guid.NewGuid(); TenantId = tenantId; TransactionId = txnId
                Amount = { Amount = -30.00m; CurrencyCode = "USD" }
                CategoryId = None; Description = Some "A"; Memo = None
                Source = SplitSource.Manual; SortOrder = 0
                CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow }
            let! _ = splitRepo.CreateAsync(split)

            let listCtx = createHttpContextWithAuth factory token
            do! SplitEndpoints.listSplitsHandler txnId listCtx

            test <@ listCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson listCtx
            let items = doc.RootElement.GetProperty("splits").EnumerateArray() |> Seq.toList
            test <@ items.Length = 1 @>
        }

    [<Fact>]
    member _.``DELETE /api/transactions/{id}/splits/{splitId} deletes split``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "splitdel@example.com"
            let jwtDoc =
                let ctx = createHttpContextWithAuth factory token
                setJsonBody ctx """{"email":"x","password":"x","displayName":"x","tenantDisplayName":"x"}"""
                Auth.registerHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
                readResponseJson ctx
            let tenantId = Guid.Parse(jwtDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(jwtDoc.RootElement.GetProperty("userId").GetString())

            use seedConn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow

            let accessor = { new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let splitRepo = SplitRepository.create factory accessor
            let splitId = Guid.NewGuid()
            let split: TransactionSplit = {
                Id = splitId; TenantId = tenantId; TransactionId = txnId
                Amount = { Amount = -30.00m; CurrencyCode = "USD" }
                CategoryId = None; Description = Some "A"; Memo = None
                Source = SplitSource.Manual; SortOrder = 0
                CreatedAt = DateTimeOffset.UtcNow; UpdatedAt = DateTimeOffset.UtcNow }
            let! _ = splitRepo.CreateAsync(split)

            let delCtx = createHttpContextWithAuth factory token
            do! SplitEndpoints.deleteSplitHandler txnId splitId delCtx

            test <@ delCtx.Response.StatusCode = 204 @>
        }

    [<Fact>]
    member _.``Sum-to-parent: splits must sum to transaction amount``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "splitsum@example.com"
            let jwtDoc =
                let ctx = createHttpContextWithAuth factory token
                setJsonBody ctx """{"email":"x","password":"x","displayName":"x","tenantDisplayName":"x"}"""
                Auth.registerHandler ctx |> Async.AwaitTask |> Async.RunSynchronously
                readResponseJson ctx
            let tenantId = Guid.Parse(jwtDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(jwtDoc.RootElement.GetProperty("userId").GetString())

            use seedConn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"amountMinor":-6000,"currency":"USD","sortOrder":0}"""
            do! SplitEndpoints.createSplitHandler txnId createCtx
            test <@ createCtx.Response.StatusCode = 201 @>

            // Second split that doesn't sum correctly should return 400
            let createCtx2 = createHttpContextWithAuth factory token
            setJsonBody createCtx2 """{"amountMinor":-6000,"currency":"USD","sortOrder":1}"""
            do! SplitEndpoints.createSplitHandler txnId createCtx2
            test <@ createCtx2.Response.StatusCode = 400 @>
        }
