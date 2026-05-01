module BitThicket.Steward.Api.Test.AttachmentRepositoryTests

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

let private seedAttachment (conn: NpgsqlConnection) (tenantId: Guid) (txnId: Guid) (attachmentId: Guid) (storageRef: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO attachments (
               id, tenant_id, transaction_id, split_id, kind, storage_ref,
               content_hash, content_type, size_bytes, uploaded_at,
               uploaded_by_user_id, uploaded_by_agent_id
           ) VALUES ($1, $2, $3, NULL, 'receipt', $4, 'abc123', 'image/jpeg', 1024, now(), NULL, NULL)"""
    cmd.Parameters.AddWithValue("$1", attachmentId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", txnId) |> ignore
    cmd.Parameters.AddWithValue("$4", storageRef) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Tests ──────────────────────────────────────────────────────────────────

type AttachmentRepositoryTests() =

    [<Fact>]
    member _.``Create and get attachment``() =
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
            let attachmentId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow
            seedAttachment seedConn tenantId txnId attachmentId "ref-1"

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let repo = AttachmentRepository.create factory accessor

            let! fetchedOpt = repo.GetAsync(attachmentId)
            test <@ fetchedOpt.IsSome @>
            let fetched = fetchedOpt.Value
            test <@ fetched.StorageRef = "ref-1" @>
            test <@ fetched.ContentType = "image/jpeg" @>
            test <@ fetched.SizeBytes = 1024L @>
        }

    [<Fact>]
    member _.``List attachments by transaction``() =
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
            seedAttachment seedConn tenantId txnId (Guid.NewGuid()) "ref-a"
            seedAttachment seedConn tenantId txnId (Guid.NewGuid()) "ref-b"

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let repo = AttachmentRepository.create factory accessor

            let! attachments = repo.ListByTransactionAsync(txnId)
            test <@ attachments.Length = 2 @>
        }

    [<Fact>]
    member _.``Delete attachment``() =
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
            let attachmentId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow
            seedAttachment seedConn tenantId txnId attachmentId "ref-1"

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let repo = AttachmentRepository.create factory accessor

            do! repo.DeleteAsync(attachmentId)
            let! fetchedOpt = repo.GetAsync(attachmentId)
            test <@ fetchedOpt.IsNone @>
        }

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot access tenant B attachment``() =
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
            let attachmentId = Guid.NewGuid()

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantA userId accountId "Checking" "USD"
            seedTransaction seedConn tenantA accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow
            seedAttachment seedConn tenantA txnId attachmentId "ref-1"

            let accessorB =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantB; UserId = userId } }
            let repoB = AttachmentRepository.create factory accessorB

            let! fetchedOpt = repoB.GetAsync(attachmentId)
            test <@ fetchedOpt.IsNone @>
        }

    [<Fact>]
    member _.``CountByStorageRefAsync reflects only current tenant``() =
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
            let attachA = Guid.NewGuid()
            let attachB = Guid.NewGuid()
            let sharedRef = "shared-ref-abc123"

            use seedConn = dataSource.OpenConnection()
            seedAccount seedConn tenantId userId accountId "Checking" "USD"
            seedTransaction seedConn tenantId accountId txnId -10000L DateTimeOffset.UtcNow "manual" "cleared" DateTimeOffset.UtcNow
            seedAttachment seedConn tenantId txnId attachA sharedRef
            seedAttachment seedConn tenantId txnId attachB sharedRef

            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let repo = AttachmentRepository.create factory accessor

            let! count = repo.CountByStorageRefAsync(sharedRef)
            test <@ count = 2 @>

            // Delete one ref.
            do! repo.DeleteAsync(attachA)
            let! countAfter = repo.CountByStorageRefAsync(sharedRef)
            test <@ countAfter = 1 @>

            // Delete the remaining ref.
            do! repo.DeleteAsync(attachB)
            let! countFinal = repo.CountByStorageRefAsync(sharedRef)
            test <@ countFinal = 0 @>
        }
