module BitThicket.Steward.Api.Test.AccountEndpointsTests

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

// ── Test helpers (reused from AuthTests / ApiKeyTests) ─────────────────────

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

let private readResponse (ctx: HttpContext) =
    ctx.Response.Body.Position <- 0L
    use reader = new StreamReader(ctx.Response.Body)
    reader.ReadToEnd()

let private readResponseJson (ctx: HttpContext) =
    let json = readResponse ctx
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

type AccountEndpointsTests() =

    [<Fact>]
    member _.``POST /api/accounts creates an account``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "create@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"My Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx

            test <@ ctx.Response.StatusCode = 201 @>
            let doc = readResponseJson ctx
            test <@ doc.RootElement.GetProperty("name").GetString() = "My Checking" @>
            test <@ doc.RootElement.GetProperty("accountType").GetString() = "checking" @>
            test <@ doc.RootElement.GetProperty("currency").GetString() = "USD" @>
            test <@ doc.RootElement.GetProperty("isOnBudget").GetBoolean() = true @>
        }

    [<Fact>]
    member _.``POST /api/accounts defaults isOnBudget per ADR-009``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "default@example.com"

            let investmentCtx = createHttpContextWithAuth factory token
            setJsonBody investmentCtx """{"name":"My Investment","accountType":"investment","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler investmentCtx
            test <@ investmentCtx.Response.StatusCode = 201 @>
            let doc = readResponseJson investmentCtx
            test <@ doc.RootElement.GetProperty("isOnBudget").GetBoolean() = false @>
        }

    [<Fact>]
    member _.``POST /api/accounts validates empty name``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "emptyname@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"   ","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx

            test <@ ctx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``POST /api/accounts validates invalid accountType``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "badtype@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Bad","accountType":"spaceship","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler ctx

            test <@ ctx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``POST /api/accounts validates invalid currency``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "badcurr@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Bad","accountType":"checking","currency":"US"}"""
            do! AccountEndpoints.createAccountHandler ctx

            test <@ ctx.Response.StatusCode = 400 @>
        }

    [<Fact>]
    member _.``GET /api/accounts lists current tenant accounts``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "list@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"name":"Account A","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler createCtx

            let listCtx = createHttpContextWithAuth factory token
            do! AccountEndpoints.listAccountsHandler listCtx

            test <@ listCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson listCtx
            let accounts = doc.RootElement.GetProperty("accounts").EnumerateArray() |> Seq.toList
            test <@ accounts.Length = 1 @>
            test <@ accounts.[0].GetProperty("name").GetString() = "Account A" @>
        }

    [<Fact>]
    member _.``GET /api/accounts/{id} returns account``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "get@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"name":"Account B","accountType":"savings","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler createCtx
            let createDoc = readResponseJson createCtx
            let accountId = Guid.Parse(createDoc.RootElement.GetProperty("id").GetString())

            let getCtx = createHttpContextWithAuth factory token
            do! AccountEndpoints.getAccountHandler accountId getCtx

            test <@ getCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson getCtx
            test <@ doc.RootElement.GetProperty("name").GetString() = "Account B" @>
        }

    [<Fact>]
    member _.``GET /api/accounts/{id} returns 404 for non-existent account``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "get404@example.com"
            let getCtx = createHttpContextWithAuth factory token
            do! AccountEndpoints.getAccountHandler (Guid.NewGuid()) getCtx

            test <@ getCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``PATCH /api/accounts/{id} updates name and isOnBudget``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "patch@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"name":"Original","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler createCtx
            let createDoc = readResponseJson createCtx
            let accountId = Guid.Parse(createDoc.RootElement.GetProperty("id").GetString())

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx """{"name":"Updated","isOnBudget":false}"""
            do! AccountEndpoints.updateAccountHandler accountId patchCtx

            test <@ patchCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson patchCtx
            test <@ doc.RootElement.GetProperty("name").GetString() = "Updated" @>
            test <@ doc.RootElement.GetProperty("isOnBudget").GetBoolean() = false @>
        }

    [<Fact>]
    member _.``PATCH /api/accounts/{id} returns 404 for non-existent account``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "patch404@example.com"
            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx """{"name":"Updated"}"""
            do! AccountEndpoints.updateAccountHandler (Guid.NewGuid()) patchCtx

            test <@ patchCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``DELETE /api/accounts/{id} soft-deletes and returns 204``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "delete@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"name":"To Delete","accountType":"cash","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler createCtx
            let createDoc = readResponseJson createCtx
            let accountId = Guid.Parse(createDoc.RootElement.GetProperty("id").GetString())

            let delCtx = createHttpContextWithAuth factory token
            do! AccountEndpoints.deleteAccountHandler accountId delCtx
            test <@ delCtx.Response.StatusCode = 204 @>

            // Should no longer appear in list/get
            let getCtx = createHttpContextWithAuth factory token
            do! AccountEndpoints.getAccountHandler accountId getCtx
            test <@ getCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``DELETE /api/accounts/{id} returns 404 for non-existent account``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "del404@example.com"
            let delCtx = createHttpContextWithAuth factory token
            do! AccountEndpoints.deleteAccountHandler (Guid.NewGuid()) delCtx

            test <@ delCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot access tenant B account``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! tokenA = registerAndGetToken factory "tenantA@example.com"
            let! tokenB = registerAndGetToken factory "tenantB@example.com"

            // Tenant B creates an account
            let createCtx = createHttpContextWithAuth factory tokenB
            setJsonBody createCtx """{"name":"B Account","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler createCtx
            let createDoc = readResponseJson createCtx
            let accountId = Guid.Parse(createDoc.RootElement.GetProperty("id").GetString())

            // Tenant A tries GET → 404 (not 403 — we do not leak existence)
            let getCtx = createHttpContextWithAuth factory tokenA
            do! AccountEndpoints.getAccountHandler accountId getCtx
            test <@ getCtx.Response.StatusCode = 404 @>

            // Tenant A tries PATCH → 404
            let patchCtx = createHttpContextWithAuth factory tokenA
            setJsonBody patchCtx """{"name":"Hacked"}"""
            do! AccountEndpoints.updateAccountHandler accountId patchCtx
            test <@ patchCtx.Response.StatusCode = 404 @>

            // Tenant A tries DELETE → 404
            let delCtx = createHttpContextWithAuth factory tokenA
            do! AccountEndpoints.deleteAccountHandler accountId delCtx
            test <@ delCtx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``Unauthenticated requests return 401``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let listCtx = createHttpContext factory
            do! AccountEndpoints.listAccountsHandler listCtx
            test <@ listCtx.Response.StatusCode = 401 @>

            let getCtx = createHttpContext factory
            do! AccountEndpoints.getAccountHandler (Guid.NewGuid()) getCtx
            test <@ getCtx.Response.StatusCode = 401 @>

            let createCtx = createHttpContext factory
            setJsonBody createCtx """{"name":"X","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler createCtx
            test <@ createCtx.Response.StatusCode = 401 @>

            let patchCtx = createHttpContext factory
            setJsonBody patchCtx """{"name":"X"}"""
            do! AccountEndpoints.updateAccountHandler (Guid.NewGuid()) patchCtx
            test <@ patchCtx.Response.StatusCode = 401 @>

            let delCtx = createHttpContext factory
            do! AccountEndpoints.deleteAccountHandler (Guid.NewGuid()) delCtx
            test <@ delCtx.Response.StatusCode = 401 @>
        }
