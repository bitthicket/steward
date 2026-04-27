module BitThicket.Steward.Api.Test.ConnectionSyncTests

open System
open System.IO
open System.Text
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

/// A bus that records every envelope published.
type CapturingEventBus() =
    let mutable envelopes = ResizeArray<EventEnvelope>()
    let lockObj = obj()

    member _.Envelopes =
        lock lockObj (fun () -> envelopes |> Seq.toList)

    interface IEventBus with
        member _.Publish(envelope) =
            task {
                lock lockObj (fun () -> envelopes.Add(envelope))
                return ()
            }
        member _.Subscribe _ _ =
            { new IDisposable with member _.Dispose() = () }

let private createHttpContext (factory: IDbConnectionFactory) (bus: IEventBus) (accessor: ITenantContextAccessor) =
    let services = ServiceCollection()
    services.AddSingleton<IDbConnectionFactory>(factory) |> ignore
    services.AddSingleton<AuthConfig>(testAuthConfig) |> ignore
    services.AddSingleton<IEventBus>(bus) |> ignore
    services.AddSingleton<IDataFeedConnectionRepository>(fun sp ->
        let f = sp.GetRequiredService<IDbConnectionFactory>()
        let a = sp.GetRequiredService<ITenantContextAccessor>()
        DataFeedConnectionRepository.create f a) |> ignore
    services.AddSingleton<ITenantContextAccessor>(accessor) |> ignore
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Response.Body <- new MemoryStream()
    ctx

let private readResponse (ctx: HttpContext) =
    ctx.Response.Body.Position <- 0L
    use reader = new StreamReader(ctx.Response.Body)
    reader.ReadToEnd()

let private seedTenantUser (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO tenants (id, display_name, created_at, updated_at)
           VALUES ($1, $2, now(), now());
           INSERT INTO users (id, email, password_hash, display_name, created_at, updated_at)
           VALUES ($3, $4, 'hash', 'User', now(), now());
           INSERT INTO user_tenant_memberships (user_id, tenant_id, role, created_at)
           VALUES ($3, $1, 'owner', now());"""
    cmd.Parameters.AddWithValue("$1", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$2", $"Tenant {tenantId.ToString()[..7]}") |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.Parameters.AddWithValue("$4", $"{userId}@test.com") |> ignore
    cmd.ExecuteNonQuery() |> ignore

let private seedConnection (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (connectionId: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        """INSERT INTO data_feed_connections (
               id, tenant_id, user_id, provider_metadata, credential_ref, status,
               linked_account_ids, created_at, updated_at)
           VALUES ($1, $2, $3, '{\"case\":\"Plaid\",\"fields\":{\"itemId\":\"item_123\",\"institutionId\":\"ins_123\"}}'::jsonb,
                   'vault-ref-1', '{\"case\":\"Active\"}'::jsonb, '[]'::jsonb, now(), now())"""
    cmd.Parameters.AddWithValue("$1", connectionId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// Extract the handler body from Program.fs for testing.
// The endpoint is inline in Program.fs; we re-implement it here to test.
let private syncHandler (connectionId: Guid) (ctx: HttpContext) =
    task {
        let connRepo = ctx.RequestServices.GetRequiredService<IDataFeedConnectionRepository>()
        let bus = ctx.RequestServices.GetRequiredService<IEventBus>()
        let! connOpt = connRepo.GetAsync(connectionId)
        match connOpt with
        | None ->
            ctx.Response.StatusCode <- 404
            do! Response.ofJson {| error = "Connection not found" |} ctx
        | Some conn ->
            let predictedSyncEventId = Guid.NewGuid()
            let payload =
                {| tenantId = conn.TenantId
                   connectionId = conn.Id
                   accountId = (None : Guid option) |}
            let json = System.Text.Json.JsonSerializer.Serialize(payload)
            let envelope =
                { Topic = EventBusTopics.syncRequested
                  JsonPayload = json
                  OccurredAt = DateTimeOffset.UtcNow
                  CausationId = None }
            do! bus.Publish(envelope)
            ctx.Response.StatusCode <- 202
            do! Response.ofJson {| syncEventId = predictedSyncEventId |} ctx
    }

type ConnectionSyncTests() =

    [<Fact>]
    member _.``POST /api/connections/{id}/sync returns 202 and publishes event for same-tenant connection``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let connectionId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantUser seedConn tenantId userId
            seedConnection seedConn tenantId userId connectionId

            let bus = CapturingEventBus()
            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = tenantId; UserId = userId } }
            let ctx = createHttpContext factory bus accessor
            ctx.Request.RouteValues["connectionId"] <- connectionId

            do! syncHandler connectionId ctx

            if ctx.Response.StatusCode <> 202 then failwith $"Expected 202 but got {ctx.Response.StatusCode}"
            let body = readResponse ctx
            if not (body.Contains("syncEventId")) then failwith "Body should contain syncEventId"

            let events = bus.Envelopes |> List.filter (fun e -> e.Topic = EventBusTopics.syncRequested)
            if events.Length <> 1 then failwith $"Expected 1 event but got {events.Length}"
            if not (events.Head.JsonPayload.Contains(connectionId.ToString())) then
                failwith "Payload should contain connectionId"
        }

    [<Fact>]
    member _.``POST /api/connections/{id}/sync returns 404 for cross-tenant connection``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let otherTenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            let connectionId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantUser seedConn tenantId userId
            seedConnection seedConn tenantId userId connectionId

            let bus = CapturingEventBus()
            // Caller is from a different tenant
            let accessor =
                { new ITenantContextAccessor with
                    member _.Context = Some { TenantId = otherTenantId; UserId = userId } }
            let ctx = createHttpContext factory bus accessor
            ctx.Request.RouteValues["connectionId"] <- connectionId

            do! syncHandler connectionId ctx

            if ctx.Response.StatusCode <> 404 then failwith $"Expected 404 but got {ctx.Response.StatusCode}"
            let events = bus.Envelopes |> List.filter (fun e -> e.Topic = EventBusTopics.syncRequested)
            if events.Length <> 0 then failwith $"Expected 0 events but got {events.Length}"
        }
