#nowarn "0044"

module BitThicket.Steward.Api.Test.CategoryRepositoryTests

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

let private seedTenantAndUser (conn: NpgsqlConnection) (tenantId: Guid) (userId: Guid) =
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

let private makeCategory (tenantId: Guid) (userId: Guid) (name: string) =
    {
        Id = Guid.NewGuid()
        TenantId = tenantId
        UserId = userId
        Name = name
        ParentCategoryId = None
        IsSystem = false
        CreatedAt = DateTimeOffset.UtcNow
    }

let private makeContext (tenantId: Guid) (userId: Guid) =
    { TenantId = tenantId; UserId = userId }

let private makeRepo (factory: IDbConnectionFactory) (ctx: TenantContext) =
    let accessor =
        { new ITenantContextAccessor with
            member _.Context = Some ctx }
    CategoryRepository.create factory accessor

type CategoryRepositoryTests() =

    [<Fact>]
    member _.``CreateAsync inserts a category and returns its id``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId

            let category = makeCategory tenantId userId "Groceries"
            let repo = makeRepo factory (makeContext tenantId userId)
            let! id = repo.CreateAsync(category)
            test <@ id = category.Id @>
        }

    [<Fact>]
    member _.``GetAsync returns the category for the current tenant``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId

            let category = makeCategory tenantId userId "Groceries"
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(category)
            let! retrieved = repo.GetAsync(category.Id)

            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Id = category.Id @>
            test <@ retrieved.Value.Name = category.Name @>
        }

    [<Fact>]
    member _.``GetAsync returns None for non-existent category``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId

            let repo = makeRepo factory (makeContext tenantId userId)
            let! retrieved = repo.GetAsync(Guid.NewGuid())
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``ListAsync returns only categories for the current tenant``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantA = Guid.NewGuid()
            let userA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()
            let userB = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantA userA
            seedTenantAndUser seedConn tenantB userB

            let catA1 = makeCategory tenantA userA "Groceries"
            let catA2 = makeCategory tenantA userA "Utilities"
            let catB = makeCategory tenantB userB "Entertainment"

            let repoA = makeRepo factory (makeContext tenantA userA)
            let repoB = makeRepo factory (makeContext tenantB userB)

            let! _ = repoA.CreateAsync(catA1)
            let! _ = repoA.CreateAsync(catA2)
            let! _ = repoB.CreateAsync(catB)

            let! listA = repoA.ListAsync()
            let! listB = repoB.ListAsync()

            test <@ listA.Length = 2 @>
            test <@ listB.Length = 1 @>
            test <@ listA |> List.exists (fun c -> c.Id = catA1.Id) @>
            test <@ listA |> List.exists (fun c -> c.Id = catA2.Id) @>
            test <@ listB |> List.exists (fun c -> c.Id = catB.Id) @>
        }

    [<Fact>]
    member _.``Cross-tenant isolation: tenant A cannot see tenant B category``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantA = Guid.NewGuid()
            let userA = Guid.NewGuid()
            let tenantB = Guid.NewGuid()
            let userB = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantA userA
            seedTenantAndUser seedConn tenantB userB

            let catB = makeCategory tenantB userB "Entertainment"
            let repoB = makeRepo factory (makeContext tenantB userB)
            let! _ = repoB.CreateAsync(catB)

            let repoA = makeRepo factory (makeContext tenantA userA)
            let! retrieved = repoA.GetAsync(catB.Id)
            test <@ retrieved |> Option.isNone @>
        }

    [<Fact>]
    member _.``UpdateAsync modifies an existing category``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId

            let category = makeCategory tenantId userId "Groceries"
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(category)

            let updated = { category with Name = "Food & Dining" }
            do! repo.UpdateAsync(updated)

            let! retrieved = repo.GetAsync(category.Id)
            test <@ retrieved |> Option.isSome @>
            test <@ retrieved.Value.Name = "Food & Dining" @>
        }

    [<Fact>]
    member _.``DeleteAsync removes a category``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let tenantId = Guid.NewGuid()
            let userId = Guid.NewGuid()
            use seedConn = dataSource.OpenConnection()
            seedTenantAndUser seedConn tenantId userId

            let category = makeCategory tenantId userId "Groceries"
            let repo = makeRepo factory (makeContext tenantId userId)
            let! _ = repo.CreateAsync(category)
            do! repo.DeleteAsync(category.Id)

            let! retrieved = repo.GetAsync(category.Id)
            test <@ retrieved |> Option.isNone @>
        }
