module BitThicket.Steward.Api.Test.RemediationAttemptRepositoryTests

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

type RemediationAttemptRepositoryTests() =
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
            member _.Context = Some { TenantId = tenantId; UserId = Guid.NewGuid() } }

    let createConnection (factory: IDbConnectionFactory) (tenantId: Guid) =
        let accessor = makeAccessor tenantId
        let repo = DataFeedConnectionRepository.create factory accessor
        let conn =
            { Id = Guid.NewGuid()
              TenantId = tenantId
              UserId = Guid.NewGuid()
              Metadata = ProviderMetadata.Plaid("item-test", "ins-test", None)
              CredentialRef = "prv_test"
              Status = ConnectionStatus.Active
              LinkedAccountIds = []
              LastSyncedAt = None
              CreatedAt = DateTimeOffset.UtcNow
              UpdatedAt = DateTimeOffset.UtcNow }
        repo.CreateAsync(conn).GetAwaiter().GetResult() |> ignore
        conn

    [<Fact>]
    member _.``CreateAsync inserts a remediation attempt``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantId = Guid.NewGuid()
            let accessor = makeAccessor tenantId
            let conn = createConnection factory tenantId
            let repo = RemediationAttemptRepository.create factory accessor

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
                  Notes = Some("Initial attempt") }

            let id = repo.CreateAsync(attempt).GetAwaiter().GetResult()
            test <@ id = attempt.Id @>

    [<Fact>]
    member _.``GetAsync returns the attempt``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantId = Guid.NewGuid()
            let accessor = makeAccessor tenantId
            let conn = createConnection factory tenantId
            let repo = RemediationAttemptRepository.create factory accessor

            let attempt =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  ConnectionId = conn.Id
                  StartedAt = DateTimeOffset.UtcNow
                  CompletedAt = None
                  ActorAgentId = None
                  ActorUserId = Some(Guid.NewGuid())
                  Strategy = "reauth-prompt"
                  Outcome = None
                  Notes = None }

            repo.CreateAsync(attempt).GetAwaiter().GetResult() |> ignore
            let retrieved = repo.GetAsync(attempt.Id).GetAwaiter().GetResult()
            test <@ retrieved.IsSome @>
            test <@ retrieved.Value.Strategy = "reauth-prompt" @>
            test <@ retrieved.Value.Outcome = None @>

    [<Fact>]
    member _.``UpdateOutcomeAsync sets outcome and completed_at``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantId = Guid.NewGuid()
            let accessor = makeAccessor tenantId
            let conn = createConnection factory tenantId
            let repo = RemediationAttemptRepository.create factory accessor

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
                  Notes = Some("Before") }

            repo.CreateAsync(attempt).GetAwaiter().GetResult() |> ignore
            (repo.UpdateOutcomeAsync attempt.Id (RemediationOutcome.StillFailing("CAPTCHA")) (Some("After"))).GetAwaiter().GetResult()

            let updated = repo.GetAsync(attempt.Id).GetAwaiter().GetResult()
            test <@ updated.IsSome @>
            test <@ updated.Value.Outcome = Some(RemediationOutcome.StillFailing("CAPTCHA")) @>
            test <@ updated.Value.Notes = Some("After") @>
            test <@ updated.Value.CompletedAt.IsSome @>

    [<Fact>]
    member _.``ListForConnectionAsync returns attempts ordered by started_at desc``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantId = Guid.NewGuid()
            let accessor = makeAccessor tenantId
            let conn = createConnection factory tenantId
            let repo = RemediationAttemptRepository.create factory accessor

            let a1 =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  ConnectionId = conn.Id
                  StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10.0)
                  CompletedAt = None
                  ActorAgentId = None
                  ActorUserId = Some(Guid.NewGuid())
                  Strategy = "first"
                  Outcome = None
                  Notes = None }

            let a2 =
                { Id = Guid.NewGuid()
                  TenantId = tenantId
                  ConnectionId = conn.Id
                  StartedAt = DateTimeOffset.UtcNow
                  CompletedAt = None
                  ActorAgentId = None
                  ActorUserId = Some(Guid.NewGuid())
                  Strategy = "second"
                  Outcome = None
                  Notes = None }

            repo.CreateAsync(a1).GetAwaiter().GetResult() |> ignore
            repo.CreateAsync(a2).GetAwaiter().GetResult() |> ignore

            let list = repo.ListForConnectionAsync(conn.Id).GetAwaiter().GetResult()
            test <@ list.Length = 2 @>
            test <@ list.[0].Strategy = "second" @>
            test <@ list.[1].Strategy = "first" @>

    [<Fact>]
    member _.``Cross-tenant: tenant A cannot read tenant B attempts``() =
        if not (canConnect ()) then ()
        else
            let factory = createFactory ()
            let tenantA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()
            let accessorA = makeAccessor tenantA
            let accessorB = makeAccessor tenantB
            let connB = createConnection factory tenantB
            let repoB = RemediationAttemptRepository.create factory accessorB

            let attempt =
                { Id = Guid.NewGuid()
                  TenantId = tenantB
                  ConnectionId = connB.Id
                  StartedAt = DateTimeOffset.UtcNow
                  CompletedAt = None
                  ActorAgentId = None
                  ActorUserId = Some(Guid.NewGuid())
                  Strategy = "reauth"
                  Outcome = None
                  Notes = None }

            repoB.CreateAsync(attempt).GetAwaiter().GetResult() |> ignore

            let repoA = RemediationAttemptRepository.create factory accessorA
            let retrieved = repoA.GetAsync(attempt.Id).GetAwaiter().GetResult()
            test <@ retrieved = None @>
