module BitThicket.Steward.Api.Test.AttachmentEndpointsTests

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

let private setMultipartBody (ctx: HttpContext) (content: byte[]) (fileName: string) (contentType: string) (kind: string) =
    let boundary = "----TestBoundary" + Guid.NewGuid().ToString("n")
    let sb = StringBuilder()
    sb.AppendLine($"------{boundary}") |> ignore
    sb.AppendLine($"Content-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"") |> ignore
    sb.AppendLine($"Content-Type: {contentType}") |> ignore
    sb.AppendLine() |> ignore
    let headerBytes = Encoding.UTF8.GetBytes(sb.ToString())
    let footerBytes = Encoding.UTF8.GetBytes($"\r\n------{boundary}--\r\n")
    let ms = new MemoryStream()
    ms.Write(headerBytes, 0, headerBytes.Length)
    ms.Write(content, 0, content.Length)
    ms.Write(footerBytes, 0, footerBytes.Length)
    ms.Position <- 0L
    ctx.Request.Body <- ms
    ctx.Request.ContentType <- $"multipart/form-data; boundary=----{boundary}"
    ctx.Request.ContentLength <- ms.Length

// ── Tests ──────────────────────────────────────────────────────────────────

type AttachmentEndpointsTests() =

    [<Fact>]
    member _.``POST /api/transactions/{id}/attachments uploads file``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "attupload@example.com"
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

            let uploadCtx = createHttpContextWithAuth factory token
            let fileBytes = Encoding.UTF8.GetBytes("fake image content")
            setMultipartBody uploadCtx fileBytes "receipt.jpg" "image/jpeg" "receipt"
            do! AttachmentEndpoints.createTransactionAttachmentHandler txnId uploadCtx

            test <@ uploadCtx.Response.StatusCode = 201 @>
            let doc = readResponseJson uploadCtx
            test <@ doc.RootElement.GetProperty("contentType").GetString() = "image/jpeg" @>
            test <@ doc.RootElement.GetProperty("kind").GetString() = "receipt" @>
        }

    [<Fact>]
    member _.``POST /api/transactions/{id}/attachments rejects unsupported MIME type``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "attmime@example.com"
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

            let uploadCtx = createHttpContextWithAuth factory token
            let fileBytes = Encoding.UTF8.GetBytes("fake exe content")
            setMultipartBody uploadCtx fileBytes "evil.exe" "application/x-msdownload" "other"
            do! AttachmentEndpoints.createTransactionAttachmentHandler txnId uploadCtx

            test <@ uploadCtx.Response.StatusCode = 415 @>
        }

    [<Fact>]
    member _.``POST /api/transactions/{id}/attachments rejects oversized file``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "attsize@example.com"
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

            let uploadCtx = createHttpContextWithAuth factory token
            let fileBytes = Array.zeroCreate<byte> (11 * 1024 * 1024) // 11 MB
            setMultipartBody uploadCtx fileBytes "huge.jpg" "image/jpeg" "receipt"
            do! AttachmentEndpoints.createTransactionAttachmentHandler txnId uploadCtx

            test <@ uploadCtx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``GET /api/attachments/{id} returns file content``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "attget@example.com"
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

            let uploadCtx = createHttpContextWithAuth factory token
            let fileBytes = Encoding.UTF8.GetBytes("hello attachment")
            setMultipartBody uploadCtx fileBytes "test.txt" "text/plain" "other"
            do! AttachmentEndpoints.createTransactionAttachmentHandler txnId uploadCtx
            let uploadDoc = readResponseJson uploadCtx
            let attachmentId = Guid.Parse(uploadDoc.RootElement.GetProperty("id").GetString())

            let getCtx = createHttpContextWithAuth factory token
            do! AttachmentEndpoints.getAttachmentHandler attachmentId getCtx

            test <@ getCtx.Response.StatusCode = 200 @>
            test <@ getCtx.Response.ContentType = "text/plain" @>
            let responseBytes = (getCtx.Response.Body :?> MemoryStream).ToArray()
            test <@ Encoding.UTF8.GetString(responseBytes) = "hello attachment" @>
        }

    [<Fact>]
    member _.``DELETE /api/attachments/{id} deletes attachment``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "attdel@example.com"
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

            let uploadCtx = createHttpContextWithAuth factory token
            let fileBytes = Encoding.UTF8.GetBytes("delete me")
            setMultipartBody uploadCtx fileBytes "test.txt" "text/plain" "other"
            do! AttachmentEndpoints.createTransactionAttachmentHandler txnId uploadCtx
            let uploadDoc = readResponseJson uploadCtx
            let attachmentId = Guid.Parse(uploadDoc.RootElement.GetProperty("id").GetString())

            let delCtx = createHttpContextWithAuth factory token
            do! AttachmentEndpoints.deleteAttachmentHandler attachmentId delCtx
            test <@ delCtx.Response.StatusCode = 204 @>

            let getCtx = createHttpContextWithAuth factory token
            do! AttachmentEndpoints.getAttachmentHandler attachmentId getCtx
            test <@ getCtx.Response.StatusCode = 404 @>
        }
