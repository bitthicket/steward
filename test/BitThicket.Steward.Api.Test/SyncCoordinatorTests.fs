module BitThicket.Steward.Api.Test.SyncCoordinatorTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Xunit
open Swensen.Unquote
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

let private nullBusLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<InProcessEventBus>.Instance
let private nullCoordLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncCoordinator>.Instance

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

let private seedConnection (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) (connectionId: Guid) (lastSyncedAt: DateTimeOffset option) (frequency: TimeSpan) =
    use cmd = conn.CreateCommand()
    let lastSyncParam =
        match lastSyncedAt with
        | Some d -> cmd.Parameters.AddWithValue("$6", d.UtcDateTime)
        | None -> cmd.Parameters.AddWithValue("$6", DBNull.Value)
    cmd.CommandText <-
        """INSERT INTO data_feed_connections (
               id, tenant_id, user_id, provider_metadata, credential_ref, status,
               linked_account_ids, preferred_sync_frequency, last_synced_at, created_at, updated_at)
           VALUES ($1, $2, $3, '{\"case\":\"Plaid\",\"fields\":{\"itemId\":\"item_123\",\"institutionId\":\"ins_123\"}}'::jsonb,
                   'vault-ref-1', '{\"case\":\"Active\"}'::jsonb, '[]'::jsonb,
                   $5, $6, now(), now())"""
    cmd.Parameters.AddWithValue("$1", connectionId) |> ignore
    cmd.Parameters.AddWithValue("$2", tenantId) |> ignore
    cmd.Parameters.AddWithValue("$3", userId) |> ignore
    let freqParam = cmd.CreateParameter()
    freqParam.ParameterName <- "$5"
    freqParam.NpgsqlDbType <- NpgsqlTypes.NpgsqlDbType.Interval
    freqParam.Value <- frequency
    cmd.Parameters.Add(freqParam) |> ignore
    cmd.Parameters.Add(lastSyncParam) |> ignore
    cmd.ExecuteNonQuery() |> ignore

type SyncCoordinatorTests() =

    [<Fact>]
    member _.``Tick emits sync.requested for connection whose last sync is older than frequency``() =
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
            // Last synced 2 hours ago, frequency is 1 hour -> due
            seedConnection seedConn tenantId userId connectionId (Some(DateTimeOffset.UtcNow.AddHours(-2.0))) (TimeSpan.FromHours(1.0))

            let bus = CapturingEventBus()
            let coord = SyncCoordinator(factory, bus, nullCoordLogger)
            let cts = new CancellationTokenSource()

            // Run one tick synchronously by calling ExecuteAsync and cancelling after a short delay.
            let runTask = coord.StartAsync(cts.Token)
            // Give the coordinator time to complete one tick loop.
            do! Task.Delay(500)
            cts.Cancel()
            try do! runTask with :? OperationCanceledException -> ()

            let envelopes = bus.Envelopes |> List.filter (fun e -> e.Topic = EventBusTopics.syncRequested)
            if envelopes.Length < 1 then failwith "Expected at least one sync.requested envelope"
            let payload = envelopes.Head.JsonPayload
            if not (payload.Contains(connectionId.ToString())) then
                failwith "Payload should contain connectionId"
        }

    [<Fact>]
    member _.``Tick does not emit sync.requested for connection whose last sync is recent``() =
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
            // Last synced 30 minutes ago, frequency is 1 hour -> not due
            seedConnection seedConn tenantId userId connectionId (Some(DateTimeOffset.UtcNow.AddMinutes(-30.0))) (TimeSpan.FromHours(1.0))

            let bus = CapturingEventBus()
            let coord = SyncCoordinator(factory, bus, nullCoordLogger)
            let cts = new CancellationTokenSource()

            let runTask = coord.StartAsync(cts.Token)
            do! Task.Delay(500)
            cts.Cancel()
            try do! runTask with :? OperationCanceledException -> ()

            let envelopes = bus.Envelopes |> List.filter (fun e -> e.Topic = EventBusTopics.syncRequested)
            if envelopes.Length <> 0 then failwith "Expected zero sync.requested envelopes"
        }
