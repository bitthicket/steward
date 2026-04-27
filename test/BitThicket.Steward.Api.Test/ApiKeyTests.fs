module BitThicket.Steward.Api.Test.ApiKeyTests

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
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

// ── Test helpers (reused from AuthTests) ─────────────────────────────────────

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

let private createHttpContextWithApiKey (factory: IDbConnectionFactory) (apiKey: string) =
    let ctx = createHttpContext factory
    ctx.Request.Headers["x-api-key"] <- apiKey
    ctx

let private setJsonBody (ctx: HttpContext) (json: string) =
    let bytes = Encoding.UTF8.GetBytes(json)
    ctx.Request.Body <- new MemoryStream(bytes)
    ctx.Request.ContentType <- "application/json"
    ctx.Request.ContentLength <- int64 bytes.Length

let private readResponse (ctx: HttpContext) =
    ctx.Response.Body.Position <- 0L
    use reader = new StreamReader(ctx.Response.Body)
    reader.ReadToEnd()

let private readResponseJson (ctx: HttpContext) =
    let json = readResponse ctx
    JsonDocument.Parse(json)

// ── Tests ────────────────────────────────────────────────────────────────────

type ApiKeyTests() =

    [<Fact>]
    member _.``POST /api/api-keys creates a new API key and returns it once``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            // Register a user
            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"apikey@example.com","password":"password","displayName":"API User","tenantDisplayName":"API Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            // Create an API key
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"displayName":"Test Key"}"""
            do! Auth.createApiKeyHandler createCtx

            test <@ createCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson createCtx
            test <@ doc.RootElement.GetProperty("key").GetString().StartsWith("sk_steward_") @>
            test <@ doc.RootElement.GetProperty("displayName").GetString() = "Test Key" @>
        }

    [<Fact>]
    member _.``GET /api/api-keys lists keys without exposing full key``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"listkeys@example.com","password":"password","displayName":"List User","tenantDisplayName":"List Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"displayName":"Key To List"}"""
            do! Auth.createApiKeyHandler createCtx

            let listCtx = createHttpContextWithAuth factory token
            do! Auth.listApiKeysHandler listCtx

            test <@ listCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson listCtx
            let keys = doc.RootElement.GetProperty("keys").EnumerateArray() |> Seq.toList
            test <@ keys.Length = 1 @>
            // Must NOT contain the raw key
            let rawJson = readResponse listCtx
            test <@ not (rawJson.Contains("sk_steward_")) @>
        }

    [<Fact>]
    member _.``DELETE /api/api-keys/{id} revokes the key``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"revoke@example.com","password":"password","displayName":"Revoke User","tenantDisplayName":"Revoke Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"displayName":"Key To Revoke"}"""
            do! Auth.createApiKeyHandler createCtx
            let createDoc = readResponseJson createCtx
            let keyId = Guid.Parse(createDoc.RootElement.GetProperty("id").GetString())

            let delCtx = createHttpContextWithAuth factory token
            do! Auth.revokeApiKeyHandler keyId delCtx
            test <@ delCtx.Response.StatusCode = 204 @>

            // Revoking again should 404
            let del2Ctx = createHttpContextWithAuth factory token
            do! Auth.revokeApiKeyHandler keyId del2Ctx
            test <@ del2Ctx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``X-Api-Key header sets tenant context via middleware``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"apikeyauth@example.com","password":"password","displayName":"APIKey User","tenantDisplayName":"APIKey Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"displayName":"Auth Key"}"""
            do! Auth.createApiKeyHandler createCtx
            let createDoc = readResponseJson createCtx
            let apiKey = createDoc.RootElement.GetProperty("key").GetString()

            // Use the API key through middleware
            let httpCtx = createHttpContextWithApiKey factory apiKey
            let middleware = TenantContextMiddleware(RequestDelegate(fun _ -> Task.CompletedTask), testAuthConfig, factory)
            do! middleware.InvokeAsync(httpCtx)

            Assert.True(httpCtx.Items.ContainsKey("TenantContext"))
            let tc = httpCtx.Items["TenantContext"] :?> TenantContext
            Assert.Equal(tenantId, tc.TenantId)
        }

    [<Fact>]
    member _.``Revoked API key does not authenticate``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"revokedauth@example.com","password":"password","displayName":"Revoked User","tenantDisplayName":"Revoked Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"displayName":"Revoked Key"}"""
            do! Auth.createApiKeyHandler createCtx
            let createDoc = readResponseJson createCtx
            let apiKey = createDoc.RootElement.GetProperty("key").GetString()
            let keyId = Guid.Parse(createDoc.RootElement.GetProperty("id").GetString())

            // Revoke
            let delCtx = createHttpContextWithAuth factory token
            do! Auth.revokeApiKeyHandler keyId delCtx

            // Try to auth with revoked key
            let httpCtx = createHttpContextWithApiKey factory apiKey
            let middleware = TenantContextMiddleware(RequestDelegate(fun _ -> Task.CompletedTask), testAuthConfig, factory)
            do! middleware.InvokeAsync(httpCtx)

            Assert.False(httpCtx.Items.ContainsKey("TenantContext"))
        }
