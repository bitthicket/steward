module BitThicket.Steward.Api.Test.TestHelpers

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

// ── PostgreSQL test container ──────────────────────────────────────────────

let sharedContainer : PostgreSqlContainer option =
    try
        let c =
            PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .Build()
        c.StartAsync().GetAwaiter().GetResult()
        Some c
    with _ ->
        None

let connectionString () =
    match sharedContainer with
    | Some c -> c.GetConnectionString()
    | None -> null

let canConnect () : bool =
    let cs = connectionString ()
    if String.IsNullOrWhiteSpace(cs) then false
    else
        try
            use dataSource = NpgsqlDataSource.Create(cs)
            use conn = dataSource.OpenConnection()
            true
        with _ -> false

let runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

// ── Auth ───────────────────────────────────────────────────────────────────

let testAuthConfig = {
    JwtSecret = "test-secret-key-for-unit-tests-only-do-not-use-in-production"
    JwtSecretPrevious = None
    Issuer = "steward"
    Audience = "steward-api"
}

let registerAndGetToken (factory: IDbConnectionFactory) (email: string) =
    task {
        let regCtx = DefaultHttpContext()
        let services = ServiceCollection()
        services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
        services.AddSingleton<AuthConfig>(testAuthConfig) |> ignore
        services.AddHttpContextAccessor() |> ignore
        services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
        regCtx.RequestServices <- services.BuildServiceProvider()
        regCtx.Response.Body <- new MemoryStream()
        let bytes = Encoding.UTF8.GetBytes($"""{{"email":"{email}","password":"password","displayName":"User","tenantDisplayName":"Tenant"}}""")
        regCtx.Request.Body <- new MemoryStream(bytes)
        regCtx.Request.ContentType <- "application/json"
        regCtx.Request.ContentLength <- int64 bytes.Length
        do! Auth.registerHandler regCtx
        regCtx.Response.Body.Position <- 0L
        use reader = new StreamReader(regCtx.Response.Body)
        let! json = reader.ReadToEndAsync()
        let doc = JsonDocument.Parse(json)
        return doc.RootElement.GetProperty("accessToken").GetString()
    }

// ── HTTP context helpers ───────────────────────────────────────────────────

let createHttpContext (factory: IDbConnectionFactory) =
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
    services.AddSingleton<ISplitRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        SplitRepository.create f accessor) |> ignore
    services.AddSingleton<IAttachmentRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        AttachmentRepository.create f accessor) |> ignore
    services.AddSingleton<IAttachmentStorage>(LocalAttachmentStorage.create()) |> ignore
    services.AddHttpContextAccessor() |> ignore
    services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Response.Body <- new MemoryStream()
    ctx

let createHttpContextWithAuth (factory: IDbConnectionFactory) (token: string) =
    let ctx = createHttpContext factory
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"
    ctx

let setJsonBody (ctx: HttpContext) (json: string) =
    let bytes = Encoding.UTF8.GetBytes(json)
    ctx.Request.Body <- new MemoryStream(bytes)
    ctx.Request.ContentType <- "application/json"
    ctx.Request.ContentLength <- int64 bytes.Length

let readResponse (ctx: HttpContext) =
    ctx.Response.Body.Position <- 0L
    use reader = new StreamReader(ctx.Response.Body)
    reader.ReadToEnd()

let readResponseJson (ctx: HttpContext) =
    let json = readResponse ctx
    JsonDocument.Parse(json)

// ── Seeding helpers ────────────────────────────────────────────────────────

let seedAccount (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (accountId: Guid) (name: string) (currency: string) =
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
    cmd.Parameters.AddWithValue("$6", currency) |> ignore
    cmd.Parameters.AddWithValue("$7", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$8", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$9", DBNull.Value) |> ignore
    cmd.Parameters.AddWithValue("$10", true) |> ignore
    cmd.Parameters.AddWithValue("$11", true) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let seedTransaction (conn: NpgsqlConnection) (tenantId: Guid) (accountId: Guid) (txnId: Guid) (amountMinor: int64) (occurredAt: DateTimeOffset) (source: string) (status: string) (createdAt: DateTimeOffset) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO transactions (
               id, tenant_id, account_id, occurred_at, posted_at,
               amount_minor, currency, description, merchant, memo,
               category_id, source, external_id, matched_transaction_id, transfer_account_id,
               status, match_confidence, sync_event_id, created_at, updated_at, deleted_at
           ) VALUES ($1, $2, $3, $4, NULL, $5, 'USD', 'Test', 'Merchant', NULL, NULL,
                     $6::jsonb, NULL, NULL, NULL, $7, NULL, NULL, $8, $8, NULL)"""
    cmd.Parameters.AddWithValue("$1", txnId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", accountId) |> ignore
    cmd.Parameters.AddWithValue("$4", occurredAt.UtcDateTime) |> ignore
    cmd.Parameters.AddWithValue("$5", amountMinor) |> ignore
    let sourceParam = cmd.CreateParameter()
    sourceParam.ParameterName <- "$6"
    sourceParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Jsonb
    sourceParam.Value <- box $"""{{"type":"{source}"}}"""
    cmd.Parameters.Add(sourceParam) |> ignore
    cmd.Parameters.AddWithValue("$7", status) |> ignore
    cmd.Parameters.AddWithValue("$8", createdAt.UtcDateTime) |> ignore
    cmd.ExecuteNonQuery() |> ignore
