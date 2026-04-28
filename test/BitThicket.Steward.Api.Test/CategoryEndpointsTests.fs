module BitThicket.Steward.Api.Test.CategoryEndpointsTests

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

// ── Test helpers (reused from AccountEndpointsTests) ───────────────────────

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
    services.AddSingleton<ICategoryRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        CategoryRepository.create f accessor) |> ignore
    services.AddSingleton<ITransactionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        TransactionRepository.create f accessor) |> ignore
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

type CategoryEndpointsTests() =

    [<Fact>]
    member _.``Registration seeds default categories``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "seed@example.com"
            let ctx = createHttpContextWithAuth factory token
            do! CategoryEndpoints.listCategoriesHandler ctx

            let ctx_status = ctx.Response.StatusCode
            test <@ ctx_status = 200 @>
            let doc = readResponseJson ctx
            let categories = doc.RootElement.GetProperty("categories").EnumerateArray() |> Seq.toList
            test <@ categories.Length = 6 @>
            let names = categories |> List.map (fun c -> c.GetProperty("name").GetString()) |> Set.ofList
            test <@ names = Set(["Income"; "Housing"; "Food"; "Transportation"; "Savings"; "Uncategorized"]) @>
        }

    [<Fact>]
    member _.``POST /api/categories creates a category``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "create@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Groceries","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler ctx

            let ctx_status = ctx.Response.StatusCode
            test <@ ctx_status = 201 @>
            let doc = readResponseJson ctx
            test <@ doc.RootElement.GetProperty("name").GetString() = "Groceries" @>
            test <@ doc.RootElement.GetProperty("currency").GetString() = "USD" @>
            test <@ doc.RootElement.GetProperty("rolloverEnabled").GetBoolean() = false @>
        }

    [<Fact>]
    member _.``POST /api/categories with parentId creates child``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "parent@example.com"

            let parentCtx = createHttpContextWithAuth factory token
            setJsonBody parentCtx """{"name":"Food","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler parentCtx
            let parentDoc = readResponseJson parentCtx
            let parentId = Guid.Parse(parentDoc.RootElement.GetProperty("id").GetString())

            let childCtx = createHttpContextWithAuth factory token
            setJsonBody childCtx $"{{\"name\":\"Dining\",\"parentId\":\"{parentId}\",\"currency\":\"USD\"}}"
            do! CategoryEndpoints.createCategoryHandler childCtx

            let childCtx_status = childCtx.Response.StatusCode
            test <@ childCtx_status = 201 @>
            let childDoc = readResponseJson childCtx
            let childParentId = childDoc.RootElement.GetProperty("parentId").GetString()
            Assert.Equal(parentId.ToString(), childParentId)
        }

    [<Fact>]
    member _.``POST /api/categories rejects cycle``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "cycle@example.com"

            let aCtx = createHttpContextWithAuth factory token
            setJsonBody aCtx """{"name":"A","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler aCtx
            let aId = Guid.Parse((readResponseJson aCtx).RootElement.GetProperty("id").GetString())

            let bCtx = createHttpContextWithAuth factory token
            setJsonBody bCtx $"{{\"name\":\"B\",\"parentId\":\"{aId}\",\"currency\":\"USD\"}}"
            do! CategoryEndpoints.createCategoryHandler bCtx
            let bId = Guid.Parse((readResponseJson bCtx).RootElement.GetProperty("id").GetString())

            let cCtx = createHttpContextWithAuth factory token
            setJsonBody cCtx $"{{\"name\":\"C\",\"parentId\":\"{bId}\",\"currency\":\"USD\"}}"
            do! CategoryEndpoints.createCategoryHandler cCtx
            let cId = Guid.Parse((readResponseJson cCtx).RootElement.GetProperty("id").GetString())

            // Try to make A's parent = C — cycle!
            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx $"{{\"parentId\":\"{cId}\"}}"
            do! CategoryEndpoints.updateCategoryHandler aId patchCtx

            let patchCtx_status = patchCtx.Response.StatusCode
            test <@ patchCtx_status = 400 @>
        }

    [<Fact>]
    member _.``POST /api/categories validates empty name``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "emptyname@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"   ","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler ctx

            let ctx_status = ctx.Response.StatusCode
            test <@ ctx_status = 400 @>
        }

    [<Fact>]
    member _.``POST /api/categories validates invalid currency``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "badcurr@example.com"
            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"name":"Bad","currency":"US"}"""
            do! CategoryEndpoints.createCategoryHandler ctx

            let ctx_status = ctx.Response.StatusCode
            test <@ ctx_status = 400 @>
        }

    [<Fact>]
    member _.``GET /api/categories lists categories``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "list@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"name":"Custom","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler createCtx

            let listCtx = createHttpContextWithAuth factory token
            do! CategoryEndpoints.listCategoriesHandler listCtx

            let listCtx_status = listCtx.Response.StatusCode
            test <@ listCtx_status = 200 @>
            let doc = readResponseJson listCtx
            let categories = doc.RootElement.GetProperty("categories").EnumerateArray() |> Seq.toList
            test <@ categories.Length = 7 @> // 6 defaults + 1 custom
        }

    [<Fact>]
    member _.``GET /api/categories?tree=true returns nested tree``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "tree@example.com"

            let parentCtx = createHttpContextWithAuth factory token
            setJsonBody parentCtx """{"name":"Food","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler parentCtx
            let parentId = Guid.Parse((readResponseJson parentCtx).RootElement.GetProperty("id").GetString())

            let childCtx = createHttpContextWithAuth factory token
            setJsonBody childCtx $"{{\"name\":\"Dining\",\"parentId\":\"{parentId}\",\"currency\":\"USD\"}}"
            do! CategoryEndpoints.createCategoryHandler childCtx

            let listCtx = createHttpContextWithAuth factory token
            listCtx.Request.QueryString <- Microsoft.AspNetCore.Http.QueryString("?tree=true")
            do! CategoryEndpoints.listCategoriesHandler listCtx

            let listStatus = listCtx.Response.StatusCode
            test <@ listStatus = 200 @>
            let doc = readResponseJson listCtx
            let categories = doc.RootElement.GetProperty("categories").EnumerateArray() |> Seq.toList
            // Food should be a root node with Dining as child
            let foodNode = categories |> List.tryFind (fun c -> c.GetProperty("name").GetString() = "Food")
            test <@ foodNode.IsSome @>
            let children = foodNode.Value.GetProperty("children").EnumerateArray() |> Seq.toList
            test <@ children.Length = 1 @>
            test <@ children.[0].GetProperty("name").GetString() = "Dining" @>
        }

    [<Fact>]
    member _.``GET /api/categories/{id} returns category``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "get@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"name":"GetMe","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler createCtx
            let catId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let getCtx = createHttpContextWithAuth factory token
            do! CategoryEndpoints.getCategoryHandler catId getCtx

            let getCtx_status = getCtx.Response.StatusCode
            test <@ getCtx_status = 200 @>
            let doc = readResponseJson getCtx
            test <@ doc.RootElement.GetProperty("name").GetString() = "GetMe" @>
        }

    [<Fact>]
    member _.``GET /api/categories/{id} returns 404 for non-existent``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "get404@example.com"
            let getCtx = createHttpContextWithAuth factory token
            do! CategoryEndpoints.getCategoryHandler (Guid.NewGuid()) getCtx

            let getCtx_status = getCtx.Response.StatusCode
            test <@ getCtx_status = 404 @>
        }

    [<Fact>]
    member _.``PATCH /api/categories/{id} updates name and rollover``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "patch@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"name":"Original","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler createCtx
            let catId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let patchCtx = createHttpContextWithAuth factory token
            setJsonBody patchCtx """{"name":"Updated","rolloverEnabled":true}"""
            do! CategoryEndpoints.updateCategoryHandler catId patchCtx

            let patchCtx_status = patchCtx.Response.StatusCode
            test <@ patchCtx_status = 200 @>
            let doc = readResponseJson patchCtx
            test <@ doc.RootElement.GetProperty("name").GetString() = "Updated" @>
            test <@ doc.RootElement.GetProperty("rolloverEnabled").GetBoolean() = true @>
        }

    [<Fact>]
    member _.``DELETE /api/categories/{id} soft-deletes and returns 204``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "delete@example.com"
            let createCtx = createHttpContextWithAuth factory token
            setJsonBody createCtx """{"name":"ToDelete","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler createCtx
            let catId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let delCtx = createHttpContextWithAuth factory token
            do! CategoryEndpoints.deleteCategoryHandler catId delCtx
            let delCtx_status = delCtx.Response.StatusCode
            test <@ delCtx_status = 204 @>

            let getCtx = createHttpContextWithAuth factory token
            do! CategoryEndpoints.getCategoryHandler catId getCtx
            let getCtx_status = getCtx.Response.StatusCode
            test <@ getCtx_status = 404 @>
        }

    [<Fact>]
    member _.``DELETE /api/categories/{id} rejects when transactions exist``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "delblock@example.com"

            // Create an account
            let services = ServiceCollection()
            services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
            services.AddSingleton<IAccountRepository>(fun sp ->
                let f = sp.GetRequiredService<IDbConnectionFactory>()
                let accessor = sp.GetRequiredService<ITenantContextAccessor>()
                AccountRepository.create f accessor) |> ignore
            services.AddSingleton<ICategoryRepository>(fun sp ->
                let f = sp.GetRequiredService<IDbConnectionFactory>()
                let accessor = sp.GetRequiredService<ITenantContextAccessor>()
                CategoryRepository.create f accessor) |> ignore
            services.AddHttpContextAccessor() |> ignore
            services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
            let provider = services.BuildServiceProvider()

            let accCtx = DefaultHttpContext()
            accCtx.RequestServices <- provider
            accCtx.Response.Body <- new MemoryStream()
            accCtx.Request.Headers["Authorization"] <- $"Bearer {token}"
            setJsonBody accCtx """{"name":"Checking","accountType":"checking","currency":"USD"}"""
            do! AccountEndpoints.createAccountHandler accCtx
            let accountId = Guid.Parse((readResponseJson accCtx).RootElement.GetProperty("id").GetString())

            let catCtx = createHttpContextWithAuth factory token
            setJsonBody catCtx """{"name":"Groceries","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler catCtx
            let catId = Guid.Parse((readResponseJson catCtx).RootElement.GetProperty("id").GetString())

            // Create a transaction with this category
            let txnCtx = DefaultHttpContext()
            txnCtx.RequestServices <- provider
            txnCtx.Response.Body <- new MemoryStream()
            txnCtx.Request.Headers["Authorization"] <- $"Bearer {token}"
            setJsonBody txnCtx $"{{\"accountId\":\"{accountId}\",\"occurredAt\":\"2024-01-01T00:00:00Z\",\"amountMinor\":1000,\"currency\":\"USD\",\"description\":\"Test\",\"categoryId\":\"{catId}\"}}"
            do! TransactionEndpoints.createTransactionHandler txnCtx

            // Try to delete — should be rejected
            let delCtx = createHttpContextWithAuth factory token
            do! CategoryEndpoints.deleteCategoryHandler catId delCtx
            let delCtx_status = delCtx.Response.StatusCode
            test <@ delCtx_status = 409 @>
        }

    [<Fact>]
    member _.``DELETE /api/categories/{id}?reassignTo migrates and deletes``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "delreassign@example.com"

            let catCtx = createHttpContextWithAuth factory token
            setJsonBody catCtx """{"name":"From","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler catCtx
            let fromId = Guid.Parse((readResponseJson catCtx).RootElement.GetProperty("id").GetString())

            let toCtx = createHttpContextWithAuth factory token
            setJsonBody toCtx """{"name":"To","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler toCtx
            let toId = Guid.Parse((readResponseJson toCtx).RootElement.GetProperty("id").GetString())

            let delCtx = createHttpContextWithAuth factory token
            delCtx.Request.QueryString <- Microsoft.AspNetCore.Http.QueryString($"?reassignTo={toId}")
            do! CategoryEndpoints.deleteCategoryHandler fromId delCtx
            let delStatus = delCtx.Response.StatusCode
            test <@ delStatus = 204 @>
        }

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot access tenant B category``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! tokenA = registerAndGetToken factory "tenantA2@example.com"
            let! tokenB = registerAndGetToken factory "tenantB2@example.com"

            let createCtx = createHttpContextWithAuth factory tokenB
            setJsonBody createCtx """{"name":"B Category","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler createCtx
            let catId = Guid.Parse((readResponseJson createCtx).RootElement.GetProperty("id").GetString())

            let getCtx = createHttpContextWithAuth factory tokenA
            do! CategoryEndpoints.getCategoryHandler catId getCtx
            let getCtx_status = getCtx.Response.StatusCode
            test <@ getCtx_status = 404 @>

            let patchCtx = createHttpContextWithAuth factory tokenA
            setJsonBody patchCtx """{"name":"Hacked"}"""
            do! CategoryEndpoints.updateCategoryHandler catId patchCtx
            let patchCtx_status = patchCtx.Response.StatusCode
            test <@ patchCtx_status = 404 @>

            let delCtx = createHttpContextWithAuth factory tokenA
            do! CategoryEndpoints.deleteCategoryHandler catId delCtx
            let delCtx_status = delCtx.Response.StatusCode
            test <@ delCtx_status = 404 @>
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
            do! CategoryEndpoints.listCategoriesHandler listCtx
            let listCtx_status = listCtx.Response.StatusCode
            test <@ listCtx_status = 401 @>

            let getCtx = createHttpContext factory
            do! CategoryEndpoints.getCategoryHandler (Guid.NewGuid()) getCtx
            let getCtx_status = getCtx.Response.StatusCode
            test <@ getCtx_status = 401 @>

            let createCtx = createHttpContext factory
            setJsonBody createCtx """{"name":"X","currency":"USD"}"""
            do! CategoryEndpoints.createCategoryHandler createCtx
            let createCtx_status = createCtx.Response.StatusCode
            test <@ createCtx_status = 401 @>

            let patchCtx = createHttpContext factory
            setJsonBody patchCtx """{"name":"X"}"""
            do! CategoryEndpoints.updateCategoryHandler (Guid.NewGuid()) patchCtx
            let patchCtx_status = patchCtx.Response.StatusCode
            test <@ patchCtx_status = 401 @>

            let delCtx = createHttpContext factory
            do! CategoryEndpoints.deleteCategoryHandler (Guid.NewGuid()) delCtx
            let delCtx_status = delCtx.Response.StatusCode
            test <@ delCtx_status = 401 @>
        }
