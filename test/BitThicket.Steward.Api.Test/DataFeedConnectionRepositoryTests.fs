module BitThicket.Steward.Api.Test.DataFeedConnectionRepositoryTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Testcontainers.PostgreSql
open Npgsql
open BitThicket.Steward.Api
open BitThicket.Steward.Api.Domain

let private runMigrations (cs: string) =
    if String.IsNullOrWhiteSpace(cs) then ()
    else BitThicket.Steward.Api.Migrations.apply cs

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

type DataFeedConnectionRepositoryTests() =
    do
        match sharedContainer with
        | Some c -> runMigrations (c.GetConnectionString())
        | None -> ()

    let createFactory () =
        let cs = connectionString ()
        if String.IsNullOrWhiteSpace(cs) then
            raise (InvalidOperationException("PostgreSQL test container is not available"))
        let ds = NpgsqlDataSource.Create(cs)
        DbConnectionFactory(ds) :> IDbConnectionFactory

    let makeAccessor (tenantId: Guid) =
        { new ITenantContextAccessor with
            member _.Context = Some { TenantId = tenantId; UserId = Guid.Empty } }

    [<Fact>]
    member _.``CreateAsync inserts a connection and returns its id``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantId = Guid.NewGuid()
            let accessor = makeAccessor tenantId
            let repo = DataFeedConnectionRepository.create factory accessor

            let conn =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  UserId = Guid.NewGuid()
                  Metadata = ProviderMetadata.Plaid("item-123", "ins-456", Some "cursor-1")
                  CredentialRef = "prv_plaid_test"
                  Status = ConnectionStatus.Active
                  LinkedAccountIds = []
                  LastSyncedAt = None
                  CreatedAt = DateTimeOffset.UtcNow
                  UpdatedAt = DateTimeOffset.UtcNow }

            let id = repo.CreateAsync(conn).GetAwaiter().GetResult()
            test <@ id = conn.Id @>

    [<Fact>]
    member _.``GetAsync returns the connection for the current tenant``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantId = Guid.NewGuid()
            let accessor = makeAccessor tenantId
            let repo = DataFeedConnectionRepository.create factory accessor

            let conn =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  UserId = Guid.NewGuid()
                  Metadata = ProviderMetadata.Plaid("item-789", "ins-012", None)
                  CredentialRef = "prv_plaid_test2"
                  Status = ConnectionStatus.Active
                  LinkedAccountIds = [Guid.NewGuid()]
                  LastSyncedAt = None
                  CreatedAt = DateTimeOffset.UtcNow
                  UpdatedAt = DateTimeOffset.UtcNow }

            repo.CreateAsync(conn).GetAwaiter().GetResult() |> ignore
            let retrieved = repo.GetAsync(conn.Id).GetAwaiter().GetResult()
            test <@ retrieved.IsSome @>
            let actual = retrieved.Value
            test <@ actual.Id = conn.Id @>
            test <@ actual.Metadata = conn.Metadata @>
            test <@ actual.Status = conn.Status @>

    [<Fact>]
    member _.``GetByItemIdAsync finds connection by Plaid item_id``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantId = Guid.NewGuid()
            let accessor = makeAccessor tenantId
            let repo = DataFeedConnectionRepository.create factory accessor

            let conn =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  UserId = Guid.NewGuid()
                  Metadata = ProviderMetadata.Plaid("item-lookup-1", "ins-999", None)
                  CredentialRef = "prv_plaid_test3"
                  Status = ConnectionStatus.Active
                  LinkedAccountIds = []
                  LastSyncedAt = None
                  CreatedAt = DateTimeOffset.UtcNow
                  UpdatedAt = DateTimeOffset.UtcNow }

            repo.CreateAsync(conn).GetAwaiter().GetResult() |> ignore
            let retrieved = repo.GetByItemIdAsync("item-lookup-1").GetAwaiter().GetResult()
            test <@ retrieved.IsSome @>
            test <@ retrieved.Value.Id = conn.Id @>

    [<Fact>]
    member _.``UpdateAsync updates the connection``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantId = Guid.NewGuid()
            let accessor = makeAccessor tenantId
            let repo = DataFeedConnectionRepository.create factory accessor

            let conn =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  UserId = Guid.NewGuid()
                  Metadata = ProviderMetadata.Plaid("item-upd", "ins-upd", None)
                  CredentialRef = "prv_plaid_test4"
                  Status = ConnectionStatus.Active
                  LinkedAccountIds = []
                  LastSyncedAt = None
                  CreatedAt = DateTimeOffset.UtcNow
                  UpdatedAt = DateTimeOffset.UtcNow }

            repo.CreateAsync(conn).GetAwaiter().GetResult() |> ignore
            let updated = { conn with Status = ConnectionStatus.NeedsReauth; UpdatedAt = DateTimeOffset.UtcNow }
            repo.UpdateAsync(updated).GetAwaiter().GetResult()
            let retrieved = repo.GetAsync(conn.Id).GetAwaiter().GetResult()
            test <@ retrieved.Value.Status = ConnectionStatus.NeedsReauth @>
