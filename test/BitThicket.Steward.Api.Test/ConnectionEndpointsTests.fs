module BitThicket.Steward.Api.Test.ConnectionEndpointsTests

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

// ── Test helpers ───────────────────────────────────────────────────────────

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
    services.AddSingleton<IDataFeedConnectionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        DataFeedConnectionRepository.create f accessor) |> ignore
    services.AddSingleton<ISyncEventRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        SyncEventRepository.create f accessor) |> ignore
    services.AddSingleton<IFeedHealthRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        FeedHealthRepository.create f accessor) |> ignore
    services.AddSingleton<IRemediationAttemptRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let accessor = sp.GetRequiredService<ITenantContextAccessor>()
        RemediationAttemptRepository.create f accessor) |> ignore
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

let private createConnection (factory: IDbConnectionFactory) (tenantId: Guid) =
    let accessor = { new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = Guid.NewGuid() } }
    let repo = DataFeedConnectionRepository.create factory accessor
    let conn =
        { Id = Guid.NewGuid()
          TenantId = tenantId
          UserId = Guid.NewGuid()
          Metadata = ProviderMetadata.Plaid("item-test", "ins-test", None)
          CredentialRef = "prv_test"
          Status = ConnectionStatus.Active
          LinkedAccountIds = []
          CreatedAt = DateTimeOffset.UtcNow
          UpdatedAt = DateTimeOffset.UtcNow }
    repo.CreateAsync(conn).GetAwaiter().GetResult() |> ignore
    conn

let private createSyncEvent (factory: IDbConnectionFactory) (tenantId: Guid) (connectionId: Guid) (status: SyncStatus) =
    let accessor = { new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = Guid.Empty } }
    let repo = SyncEventRepository.create factory accessor
    let se =
        { Id = Guid.NewGuid()
          TenantId = tenantId
          ConnectionId = connectionId
          StartedAt = DateTimeOffset.UtcNow
          CompletedAt = Some(DateTimeOffset.UtcNow)
          Status = status
          TransactionsAdded = 1
          TransactionsUpdated = 0 }
    repo.CreateAsync(se).GetAwaiter().GetResult() |> ignore
    se

let private createFeedHealth (factory: IDbConnectionFactory) (tenantId: Guid) (connectionId: Guid) (level: FeedHealthLevel) =
    let accessor = { new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = Guid.Empty } }
    let repo = FeedHealthRepository.create factory accessor
    let fh =
        { ConnectionId = connectionId
          TenantId = tenantId
          Level = level
          LastSuccessAt = Some(DateTimeOffset.UtcNow)
          LastFailureAt = None
          ConsecutiveFailures = 0
          OpenRemediationAttemptId = None
          EvaluatedAt = DateTimeOffset.UtcNow }
    repo.UpsertAsync(fh).GetAwaiter().GetResult()

// ── Tests ────────────────────────────────────────────────────────────────────

type ConnectionEndpointsTests() =

    [<Fact>]
    member _.``GET /api/connections lists connections with feedHealth``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "connlist@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())

            let conn = createConnection factory tenantId
            createFeedHealth factory tenantId conn.Id FeedHealthLevel.Healthy

            let ctx = createHttpContextWithAuth factory token
            do! ConnectionEndpoints.listConnectionsHandler ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx
            let connections = doc.RootElement.GetProperty("connections").EnumerateArray() |> Seq.toList
            test <@ connections.Length = 1 @>
            test <@ connections.[0].GetProperty("id").GetString() = conn.Id.ToString() @>
            test <@ connections.[0].GetProperty("provider").GetString() = "plaid" @>
            test <@ connections.[0].GetProperty("feedHealth").ValueKind <> JsonValueKind.Null @>
            test <@ connections.[0].GetProperty("feedHealth").GetProperty("level").GetString() = "healthy" @>
        }

    [<Fact>]
    member _.``GET /api/connections/{id}/health-history returns sync events and attempts``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "history@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())

            let conn = createConnection factory tenantId
            createSyncEvent factory tenantId conn.Id SyncStatus.Success

            let ctx = createHttpContextWithAuth factory token
            ctx.Request.RouteValues.["connectionId"] <- conn.Id
            do! ConnectionEndpoints.healthHistoryHandler conn.Id ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx
            let syncEvents = doc.RootElement.GetProperty("syncEvents").EnumerateArray() |> Seq.toList
            test <@ syncEvents.Length = 1 @>
        }

    [<Fact>]
    member _.``POST /api/connections/{id}/remediation-attempts creates attempt``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "remcreate@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())

            let conn = createConnection factory tenantId

            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"strategy":"refresh-token","notes":"First try"}"""
            ctx.Request.RouteValues.["connectionId"] <- conn.Id
            do! ConnectionEndpoints.createRemediationAttemptHandler conn.Id ctx

            test <@ ctx.Response.StatusCode = 201 @>
            let doc = readResponseJson ctx
            test <@ doc.RootElement.GetProperty("strategy").GetString() = "refresh-token" @>
            test <@ doc.RootElement.GetProperty("outcome").ValueKind = JsonValueKind.Null @>
        }

    [<Fact>]
    member _.``PATCH /api/remediation-attempts/{id} resolves attempt``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "rempatch@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())

            let conn = createConnection factory tenantId
            let accessor = { new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = Guid.NewGuid() } }
            let attemptRepo = RemediationAttemptRepository.create factory accessor
            let attempt =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  ConnectionId = conn.Id
                  StartedAt = DateTimeOffset.UtcNow
                  CompletedAt = None
                  ActorAgentId = None
                  ActorUserId = Some(Guid.NewGuid())
                  Strategy = "refresh-token"
                  Outcome = None
                  Notes = None }
            attemptRepo.CreateAsync(attempt).GetAwaiter().GetResult() |> ignore

            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"outcome":"resolved","notes":"Fixed"}"""
            ctx.Request.RouteValues.["attemptId"] <- attempt.Id
            do! ConnectionEndpoints.updateRemediationAttemptHandler attempt.Id ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx
            test <@ doc.RootElement.GetProperty("outcome").GetString() = "resolved" @>
            test <@ doc.RootElement.GetProperty("completedAt").ValueKind <> JsonValueKind.Null @>
        }

    [<Fact>]
    member _.``PATCH /api/remediation-attempts/{id} returns 409 when already resolved``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! token = registerAndGetToken factory "rem409@example.com"
            let regDoc = readResponseJson (createHttpContextWithAuth factory token)
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())

            let conn = createConnection factory tenantId
            let accessor = { new ITenantContextAccessor with member _.Context = Some { TenantId = tenantId; UserId = Guid.NewGuid() } }
            let attemptRepo = RemediationAttemptRepository.create factory accessor
            let attempt =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  ConnectionId = conn.Id
                  StartedAt = DateTimeOffset.UtcNow
                  CompletedAt = Some(DateTimeOffset.UtcNow)
                  ActorAgentId = None
                  ActorUserId = Some(Guid.NewGuid())
                  Strategy = "refresh-token"
                  Outcome = Some(RemediationOutcome.Resolved)
                  Notes = None }
            attemptRepo.CreateAsync(attempt).GetAwaiter().GetResult() |> ignore

            let ctx = createHttpContextWithAuth factory token
            setJsonBody ctx """{"outcome":"resolved"}"""
            ctx.Request.RouteValues.["attemptId"] <- attempt.Id
            do! ConnectionEndpoints.updateRemediationAttemptHandler attempt.Id ctx

            test <@ ctx.Response.StatusCode = 409 @>
        }

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot access tenant B connection health-history``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! tokenA = registerAndGetToken factory "cta@example.com"
            let! tokenB = registerAndGetToken factory "ctb@example.com"
            let regDocB = readResponseJson (createHttpContextWithAuth factory tokenB)
            let tenantB = Guid.Parse(regDocB.RootElement.GetProperty("tenantId").GetString())

            let connB = createConnection factory tenantB
            createSyncEvent factory tenantB connB.Id SyncStatus.Success

            let ctx = createHttpContextWithAuth factory tokenA
            ctx.Request.RouteValues.["connectionId"] <- connB.Id
            do! ConnectionEndpoints.healthHistoryHandler connB.Id ctx

            test <@ ctx.Response.StatusCode = 404 @>
        }

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot create remediation on tenant B connection``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let! tokenA = registerAndGetToken factory "ctrem@example.com"
            let! tokenB = registerAndGetToken factory "ctremb@example.com"
            let regDocB = readResponseJson (createHttpContextWithAuth factory tokenB)
            let tenantB = Guid.Parse(regDocB.RootElement.GetProperty("tenantId").GetString())

            let connB = createConnection factory tenantB

            let ctx = createHttpContextWithAuth factory tokenA
            setJsonBody ctx """{"strategy":"refresh-token"}"""
            ctx.Request.RouteValues.["connectionId"] <- connB.Id
            do! ConnectionEndpoints.createRemediationAttemptHandler connB.Id ctx

            test <@ ctx.Response.StatusCode = 404 @>
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
            do! ConnectionEndpoints.listConnectionsHandler listCtx
            test <@ listCtx.Response.StatusCode = 401 @>

            let histCtx = createHttpContext factory
            do! ConnectionEndpoints.healthHistoryHandler (Guid.NewGuid()) histCtx
            test <@ histCtx.Response.StatusCode = 401 @>

            let remCtx = createHttpContext factory
            setJsonBody remCtx """{"strategy":"x"}"""
            do! ConnectionEndpoints.createRemediationAttemptHandler (Guid.NewGuid()) remCtx
            test <@ remCtx.Response.StatusCode = 401 @>

            let patchCtx = createHttpContext factory
            setJsonBody patchCtx """{"outcome":"resolved"}"""
            do! ConnectionEndpoints.updateRemediationAttemptHandler (Guid.NewGuid()) patchCtx
            test <@ patchCtx.Response.StatusCode = 401 @>
        }
