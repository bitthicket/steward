module BitThicket.Steward.Api.Test.SplitAttachmentEndpointsTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Swensen.Unquote
open Falco
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

// ── Test helpers (shared patterns from ExportEndpointsTests) ──────────────

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

let private runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

let private testAuthConfig = {
    JwtSecret = "test-secret-key-for-unit-tests-only-do-not-use-in-production"
    JwtSecretPrevious = None
    Issuer = "steward"
    Audience = "steward-api"
}

let private createHttpContext (factory: IDbConnectionFactory) =
    let services = ServiceCollection()
    services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
    services.AddSingleton<AuthConfig>(testAuthConfig) |> ignore
    services.AddSingleton<IAccountRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        AccountRepository.create f accessor) |> ignore
    services.AddSingleton<ICategoryRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        CategoryRepository.create f accessor) |> ignore
    services.AddSingleton<ITransactionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        TransactionRepository.create f accessor) |> ignore
    services.AddSingleton<IAttachmentStorage>(fun _sp ->
        LocalAttachmentStorage(Path.GetTempPath(), NullLogger<LocalAttachmentStorage>.Instance) :> IAttachmentStorage) |> ignore
    services.AddSingleton<IAttachmentRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        AttachmentRepository.create f accessor) |> ignore
    services.AddHttpContextAccessor() |> ignore
    services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Response.Body <- new MemoryStream()
    ctx

let private createHttpContextWithAuth (factory: IDbConnectionFactory) (token: string) =
    let ctx = createHttpContext factory
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"
    ctx

let private readResponse (ctx: HttpContext) =
    ctx.Response.Body.Position <- 0L
    use reader = new StreamReader(ctx.Response.Body, Encoding.UTF8)
    reader.ReadToEnd()

let private registerAndGetToken (factory: IDbConnectionFactory) (email: string) =
    task {
        let regCtx = createHttpContext factory
        let body = $"{{\"email\":\"{email}\",\"password\":\"password\",\"displayName\":\"User\",\"tenantDisplayName\":\"Tenant\"}}"
        regCtx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes(body))
        regCtx.Request.ContentType <- "application/json"
        regCtx.Request.ContentLength <- int64 body.Length
        do! Auth.registerHandler regCtx
        let regDoc = JsonDocument.Parse(readResponse regCtx)
        return regDoc.RootElement.GetProperty("accessToken").GetString()
    }

let private seedAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO accounts (
               id, tenant_id, user_id, name, account_type, currency,
               institution_name, external_id, credit_card_info,
               is_on_budget, is_active, created_at, updated_at
           ) VALUES ($1, $2, $3, 'Checking', 'checking', 'USD',
                     NULL, NULL, NULL, true, true, now(), now())"""
    cmd.Parameters.AddWithValue("$1", accountId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedTransaction (conn: NpgsqlConnection) (tenantId: Guid) (accountId: Guid) (txnId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO transactions (
               id, tenant_id, account_id, occurred_at, posted_at,
               amount_minor, currency, description, merchant, memo,
               category_id, source, external_id, matched_transaction_id, transfer_account_id,
               status, match_confidence, sync_event_id, created_at, updated_at, deleted_at
           ) VALUES ($1, $2, $3, now(), NULL, 100, 'USD', 'Test', NULL, NULL,
                     NULL, '{"type":"manual"}'::jsonb, NULL, NULL, NULL,
                     'cleared', NULL, NULL, now(), now(), NULL)"""
    cmd.Parameters.AddWithValue("$1", txnId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", accountId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedAttachment (conn: NpgsqlConnection) (tenantId: Guid) (txnId: Guid) (attachmentId: Guid) (storageRef: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO attachments (
               id, tenant_id, transaction_id, split_id, kind,
               storage_ref, content_hash, content_type, size_bytes,
               uploaded_at, uploaded_by_user_id, uploaded_by_agent_id
           ) VALUES ($1, $2, $3, NULL, 'receipt',
                     $4, $4, 'application/pdf', 1234,
                     now(), NULL, NULL)"""
    cmd.Parameters.AddWithValue("$1", attachmentId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", txnId) |> ignore
    cmd.Parameters.AddWithValue("$4", storageRef) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Tests ─────────────────────────────────────────────────────────────────

type SplitAttachmentEndpointsTests() =

    [<Fact>]
    member _.``Deleting attachment A does not remove shared file when attachment B references same storage_ref``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "attachdup@example.com"
            let regDoc = JsonDocument.Parse(readResponse (createHttpContextWithAuth factory token))
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(regDoc.RootElement.GetProperty("userId").GetString())

            use conn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let txnA = Guid.NewGuid()
            let txnB = Guid.NewGuid()
            let attachA = Guid.NewGuid()
            let attachB = Guid.NewGuid()
            seedAccount conn tenantId userId accountId
            seedTransaction conn tenantId accountId txnA
            seedTransaction conn tenantId accountId txnB

            // Use a deterministic storage_ref (content-addressed) shared by both attachments.
            let sharedRef = "deadbeef01deadbeef01deadbeef01deadbeef01deadbeef01deadbeef01deadbeef01"
            seedAttachment conn tenantId txnA attachA sharedRef
            seedAttachment conn tenantId txnB attachB sharedRef

            // Create a temp file simulating the shared blob.
            let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            Directory.CreateDirectory(tmpDir) |> ignore
            let blobPath = Path.Combine(tmpDir, sharedRef)
            File.WriteAllText(blobPath, "shared blob content")

            // Set up a DI container with the temp storage directory.
            let services = ServiceCollection()
            services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
            services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
            services.AddHttpContextAccessor() |> ignore
            services.AddSingleton<IAttachmentStorage>(fun _sp ->
                LocalAttachmentStorage(tmpDir, NullLogger<LocalAttachmentStorage>.Instance) :> IAttachmentStorage) |> ignore
            services.AddSingleton<IAttachmentRepository>(fun sp ->
                let f = sp.GetRequiredService<IDbConnectionFactory>()
                let accessor = sp.GetRequiredService<ITenantContextAccessor>()
                AttachmentRepository.create f accessor) |> ignore
            let provider = services.BuildServiceProvider()
            let accessor = provider.GetRequiredService<ITenantContextAccessor>()
            (accessor :?> TenantContextAccessor).Context <- Some { TenantId = tenantId; UserId = userId }

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- provider
            ctx.Response.Body <- new MemoryStream()
            ctx.Request.RouteValues.["attachmentId"] <- attachA

            // Act: delete attachment A
            do! SplitAttachmentEndpoints.deleteAttachmentHandler attachA ctx

            // Assert: 204
            test <@ ctx.Response.StatusCode = 204 @>

            // The shared file must still exist because attachment B references it.
            test <@ File.Exists(blobPath) @>

            // Attachment B row must still exist.
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM attachments WHERE id = $1"
            cmd.Parameters.AddWithValue("$1", attachB) |> ignore
            let countB = Convert.ToInt32(cmd.ExecuteScalar())
            test <@ countB = 1 @>

            // Attachment A row must be gone.
            use cmd2 = conn.CreateCommand()
            cmd2.CommandText <- "SELECT COUNT(*) FROM attachments WHERE id = $1"
            cmd2.Parameters.AddWithValue("$1", attachA) |> ignore
            let countA = Convert.ToInt32(cmd2.ExecuteScalar())
            test <@ countA = 0 @>

            // Now delete attachment B — the file should finally be removed.
            let ctx2 = DefaultHttpContext()
            ctx2.RequestServices <- provider
            ctx2.Response.Body <- new MemoryStream()
            ctx2.Request.RouteValues.["attachmentId"] <- attachB
            do! SplitAttachmentEndpoints.deleteAttachmentHandler attachB ctx2
            test <@ ctx2.Response.StatusCode = 204 @>
            test <@ not (File.Exists(blobPath)) @>

            Directory.Delete(tmpDir, true)
        }

    [<Fact>]
    member _.``Deleting sole attachment removes the underlying file``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "attachsole@example.com"
            let regDoc = JsonDocument.Parse(readResponse (createHttpContextWithAuth factory token))
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(regDoc.RootElement.GetProperty("userId").GetString())

            use conn = dataSource.OpenConnection()
            let accountId = Guid.NewGuid()
            let txnId = Guid.NewGuid()
            let attachId = Guid.NewGuid()
            seedAccount conn tenantId userId accountId
            seedTransaction conn tenantId accountId txnId
            let storageRef = "cafebabe02cafebabe02cafebabe02cafebabe02cafebabe02cafebabe02cafebabe02"
            seedAttachment conn tenantId txnId attachId storageRef

            let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
            Directory.CreateDirectory(tmpDir) |> ignore
            let blobPath = Path.Combine(tmpDir, storageRef)
            File.WriteAllText(blobPath, "sole blob content")

            let services = ServiceCollection()
            services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
            services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
            services.AddHttpContextAccessor() |> ignore
            services.AddSingleton<IAttachmentStorage>(fun _sp ->
                LocalAttachmentStorage(tmpDir, NullLogger<LocalAttachmentStorage>.Instance) :> IAttachmentStorage) |> ignore
            services.AddSingleton<IAttachmentRepository>(fun sp ->
                let f = sp.GetRequiredService<IDbConnectionFactory>()
                let accessor = sp.GetRequiredService<ITenantContextAccessor>()
                AttachmentRepository.create f accessor) |> ignore
            let provider = services.BuildServiceProvider()
            let accessor = provider.GetRequiredService<ITenantContextAccessor>()
            (accessor :?> TenantContextAccessor).Context <- Some { TenantId = tenantId; UserId = userId }

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- provider
            ctx.Response.Body <- new MemoryStream()
            ctx.Request.RouteValues.["attachmentId"] <- attachId

            do! SplitAttachmentEndpoints.deleteAttachmentHandler attachId ctx

            test <@ ctx.Response.StatusCode = 204 @>
            test <@ not (File.Exists(blobPath)) @>
            Directory.Delete(tmpDir, true)
        }

    [<Fact>]
    member _.``Deleting nonexistent attachment returns 404``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "attach404@example.com"
            let regDoc = JsonDocument.Parse(readResponse (createHttpContextWithAuth factory token))
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let userId = Guid.Parse(regDoc.RootElement.GetProperty("userId").GetString())

            let services = ServiceCollection()
            services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
            services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
            services.AddHttpContextAccessor() |> ignore
            services.AddSingleton<IAttachmentStorage>(fun _sp ->
                LocalAttachmentStorage(Path.GetTempPath(), NullLogger<LocalAttachmentStorage>.Instance) :> IAttachmentStorage) |> ignore
            services.AddSingleton<IAttachmentRepository>(fun sp ->
                let f = sp.GetRequiredService<IDbConnectionFactory>()
                let accessor = sp.GetRequiredService<ITenantContextAccessor>()
                AttachmentRepository.create f accessor) |> ignore
            let provider = services.BuildServiceProvider()
            let accessor = provider.GetRequiredService<ITenantContextAccessor>()
            (accessor :?> TenantContextAccessor).Context <- Some { TenantId = tenantId; UserId = userId }

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- provider
            ctx.Response.Body <- new MemoryStream()
            let missingId = Guid.NewGuid()
            ctx.Request.RouteValues.["attachmentId"] <- missingId

            do! SplitAttachmentEndpoints.deleteAttachmentHandler missingId ctx

            test <@ ctx.Response.StatusCode = 404 @>
        }
