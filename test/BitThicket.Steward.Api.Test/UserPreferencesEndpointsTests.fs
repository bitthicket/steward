module BitThicket.Steward.Api.Test.UserPreferencesEndpointsTests

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

// ── Test helpers (reused from AccountEndpointsTests) ─────────────────────

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
    services.AddSingleton<IUserPreferencesRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        UserPreferencesRepository.create f accessor) |> ignore
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

let private setJsonBody (ctx: HttpContext) (json: string) =
    let bytes = Encoding.UTF8.GetBytes(json)
    ctx.Request.Body <- new MemoryStream(bytes)
    ctx.Request.ContentType <- "application/json"
    ctx.Request.ContentLength <- int64 bytes.Length

let private readResponseJson (ctx: HttpContext) =
    ctx.Response.Body.Position <- 0L
    use reader = new StreamReader(ctx.Response.Body)
    let json = reader.ReadToEnd()
    JsonDocument.Parse(json)

let private registerAndGetToken (factory: IDbConnectionFactory) (email: string) =
    task {
        let regCtx = createHttpContext factory
        setJsonBody regCtx $"{{\"email\":\"{email}\",\"password\":\"password\",\"displayName\":\"User\",\"tenantDisplayName\":\"Tenant\"}}"
        do! Auth.registerHandler regCtx
        let regDoc = readResponseJson regCtx
        return regDoc.RootElement.GetProperty("accessToken").GetString()
    }

// ── Tests ────────────────────────────────────────────────────────────────────

type UserPreferencesEndpointsTests() =

    [<Fact>]
    member _.``GET /api/preferences returns defaults when no row exists``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "prefs-default@example.com"
            let ctx = createHttpContextWithAuth factory token
            do! UserPreferencesEndpoints.getPreferencesHandler ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx
            test <@ doc.RootElement.GetProperty("defaultDisplayCurrency").GetString() = "USD" @>
            test <@ doc.RootElement.GetProperty("defaultBudgetingStyle").GetString() = "flexible" @>
            test <@ doc.RootElement.GetProperty("preferredSyncFrequencyMinutes").GetInt32() = 60 @>
        }

    [<Fact>]
    member _.``PATCH /api/preferences updates defaultDisplayCurrency``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "prefs-update@example.com"
            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx """{"defaultDisplayCurrency":"BTC"}"""
            do! UserPreferencesEndpoints.updatePreferencesHandler patchCtx

            test <@ patchCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson patchCtx
            test <@ doc.RootElement.GetProperty("defaultDisplayCurrency").GetString() = "BTC" @>

            // GET should reflect the update
            let getCtx = createHttpContextWithAuth factory token
            do! UserPreferencesEndpoints.getPreferencesHandler getCtx
            let getDoc = readResponseJson getCtx
            test <@ getDoc.RootElement.GetProperty("defaultDisplayCurrency").GetString() = "BTC" @>
        }

    [<Fact>]
    member _.``PATCH /api/preferences validates currency code length``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "prefs-badcurr@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"defaultDisplayCurrency":"US"}"""
            do! UserPreferencesEndpoints.updatePreferencesHandler ctx

            test <@ ctx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``PATCH /api/preferences clamps sync frequency``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "prefs-sync@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"preferredSyncFrequencyMinutes":5}"""
            do! UserPreferencesEndpoints.updatePreferencesHandler ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx
            // Clamped to minimum 15
            test <@ doc.RootElement.GetProperty("preferredSyncFrequencyMinutes").GetInt32() = 15 @>
        }

    [<Fact>]
    member _.``Unauthenticated requests return 401``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let getCtx = createHttpContext factory
            do! UserPreferencesEndpoints.getPreferencesHandler getCtx
            test <@ getCtx.Response.StatusCode = 401 @>

            let patchCtx = createHttpContext factory
            setJsonBody patchCtx """{"defaultDisplayCurrency":"USD"}"""
            do! UserPreferencesEndpoints.updatePreferencesHandler patchCtx
            test <@ patchCtx.Response.StatusCode = 401 @>
        }
