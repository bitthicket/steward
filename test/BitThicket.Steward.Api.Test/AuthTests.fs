module BitThicket.Steward.Api.Test.AuthTests

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

// ── Test helpers ─────────────────────────────────────────────────────────────

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

type AuthTests() =

    [<Fact>]
    member _.``Register creates user, tenant, membership and returns JWT``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let ctx = createHttpContext factory
            setJsonBody ctx """{"email":"test@example.com","password":"secure-password-123","displayName":"Test User","tenantDisplayName":"Test Tenant"}"""

            do! Auth.registerHandler ctx

            test <@ ctx.Response.StatusCode = 200 @>
            let doc = readResponseJson ctx
            test <@ doc.RootElement.GetProperty("userId").GetString() <> null @>
            test <@ doc.RootElement.GetProperty("tenantId").GetString() <> null @>
            test <@ doc.RootElement.GetProperty("accessToken").GetString() <> null @>

            let userId = Guid.Parse(doc.RootElement.GetProperty("userId").GetString())
            let tenantId = Guid.Parse(doc.RootElement.GetProperty("tenantId").GetString())

            let! user = RootRepository.getUserById factory userId
            test <@ user |> Option.isSome @>
            test <@ user.Value.Email = "test@example.com" @>
            test <@ PasswordHash.verify "secure-password-123" user.Value.PasswordHash @>

            let! tenant = RootRepository.getTenantById factory tenantId
            test <@ tenant |> Option.isSome @>
            test <@ tenant.Value.DisplayName = "Test Tenant" @>

            let! memberships = RootRepository.listMembershipsByUser factory userId
            test <@ memberships.Length = 1 @>
            test <@ memberships.[0].TenantId = tenantId @>
            test <@ memberships.[0].Role = "owner" @>
        }

    [<Fact>]
    member _.``Register with duplicate email returns 409``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let ctx1 = createHttpContext factory
            setJsonBody ctx1 """{"email":"dup@example.com","password":"password1","displayName":"First","tenantDisplayName":"First Tenant"}"""
            do! Auth.registerHandler ctx1
            test <@ ctx1.Response.StatusCode = 200 @>

            let ctx2 = createHttpContext factory
            setJsonBody ctx2 """{"email":"dup@example.com","password":"password2","displayName":"Second","tenantDisplayName":"Second Tenant"}"""
            do! Auth.registerHandler ctx2

            test <@ ctx2.Response.StatusCode = 409 @>
            let doc = readResponseJson ctx2
            test <@ doc.RootElement.GetProperty("error").GetString() = "Email already registered" @>
        }

    [<Fact>]
    member _.``Login with correct password returns access token``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"login@example.com","password":"my-password","displayName":"Login User","tenantDisplayName":"Login Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())

            let loginCtx = createHttpContext factory
            setJsonBody loginCtx $"""{{"email":"login@example.com","password":"my-password","tenantId":"{tenantId}"}}"""
            do! Auth.loginHandler loginCtx

            test <@ loginCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson loginCtx
            test <@ doc.RootElement.GetProperty("accessToken").GetString() <> null @>
        }

    [<Fact>]
    member _.``Login with wrong password returns 401``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"badpass@example.com","password":"right-password","displayName":"User","tenantDisplayName":"Tenant"}"""
            do! Auth.registerHandler regCtx

            let loginCtx = createHttpContext factory
            setJsonBody loginCtx """{"email":"badpass@example.com","password":"wrong-password"}"""
            do! Auth.loginHandler loginCtx

            test <@ loginCtx.Response.StatusCode = 401 @>
        }

    [<Fact>]
    member _.``Login with unknown email returns 401``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let loginCtx = createHttpContext factory
            setJsonBody loginCtx """{"email":"nobody@example.com","password":"any-password"}"""
            do! Auth.loginHandler loginCtx

            test <@ loginCtx.Response.StatusCode = 401 @>
        }

    [<Fact>]
    member _.``Login with multi-tenant user and no tenantId returns membership list``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            // Register first tenant
            let regCtx1 = createHttpContext factory
            setJsonBody regCtx1 """{"email":"multi@example.com","password":"password","displayName":"Multi","tenantDisplayName":"First Tenant"}"""
            do! Auth.registerHandler regCtx1
            let regDoc1 = readResponseJson regCtx1
            let userId = Guid.Parse(regDoc1.RootElement.GetProperty("userId").GetString())
            let tenantId1 = Guid.Parse(regDoc1.RootElement.GetProperty("tenantId").GetString())

            // Create second tenant and membership manually
            let tenant2: Tenant = {
                Id = Guid.NewGuid()
                DisplayName = "Second Tenant"
                CreatedAt = DateTimeOffset.UtcNow
                UpdatedAt = DateTimeOffset.UtcNow
            }
            let! _ = RootRepository.createTenant factory tenant2

            let membership2: UserTenantMembership = {
                UserId = userId
                TenantId = tenant2.Id
                Role = "member"
                CreatedAt = DateTimeOffset.UtcNow
            }
            let! _ = RootRepository.createMembership factory membership2

            // Login without tenantId
            let loginCtx = createHttpContext factory
            setJsonBody loginCtx """{"email":"multi@example.com","password":"password"}"""
            do! Auth.loginHandler loginCtx

            test <@ loginCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson loginCtx
            let hasMemberships = doc.RootElement.TryGetProperty("memberships") |> fst
            test <@ hasMemberships @>
            let memberships = doc.RootElement.GetProperty("memberships").EnumerateArray() |> Seq.toList
            test <@ memberships.Length = 2 @>
            let hasTenant1 = memberships |> List.exists (fun m -> m.GetProperty("tenantId").GetString() = tenantId1.ToString())
            let hasTenant2 = memberships |> List.exists (fun m -> m.GetProperty("tenantId").GetString() = tenant2.Id.ToString())
            test <@ hasTenant1 @>
            test <@ hasTenant2 @>
            let hasAccessToken = doc.RootElement.TryGetProperty("accessToken") |> fst
            test <@ not hasAccessToken @>
        }

    [<Fact>]
    member _.``Login with tenantId not in memberships returns 401``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"notmember@example.com","password":"password","displayName":"User","tenantDisplayName":"My Tenant"}"""
            do! Auth.registerHandler regCtx

            let badTenantId = Guid.NewGuid()
            let loginCtx = createHttpContext factory
            setJsonBody loginCtx $"""{{"email":"notmember@example.com","password":"password","tenantId":"{badTenantId}"}}"""
            do! Auth.loginHandler loginCtx

            test <@ loginCtx.Response.StatusCode = 401 @>
        }

    [<Fact>]
    member _.``Login with single membership and no tenantId returns token``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"single@example.com","password":"password","displayName":"Single","tenantDisplayName":"Single Tenant"}"""
            do! Auth.registerHandler regCtx

            let loginCtx = createHttpContext factory
            setJsonBody loginCtx """{"email":"single@example.com","password":"password"}"""
            do! Auth.loginHandler loginCtx

            test <@ loginCtx.Response.StatusCode = 200 @>
            let doc = readResponseJson loginCtx
            let hasAccessToken = doc.RootElement.TryGetProperty("accessToken") |> fst
            let hasMemberships = doc.RootElement.TryGetProperty("memberships") |> fst
            test <@ hasAccessToken @>
            test <@ not hasMemberships @>
        }

    [<Fact>]
    member _.``JWT contains correct claims``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"claims@example.com","password":"password","displayName":"Claims","tenantDisplayName":"Claims Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let userId = regDoc.RootElement.GetProperty("userId").GetString()
            let tenantId = regDoc.RootElement.GetProperty("tenantId").GetString()
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            let config = testAuthConfig
            match Jwt.tryReadToken config.JwtSecret config.JwtSecretPrevious config.Issuer config.Audience token with
            | Jwt.ValidationResult.InvalidSignature -> Assert.True(false, "JWT signature should be valid")
            | Jwt.ValidationResult.Expired -> Assert.True(false, "JWT should not be expired")
            | Jwt.ValidationResult.InvalidIssuer -> Assert.True(false, "JWT issuer should be valid")
            | Jwt.ValidationResult.InvalidAudience -> Assert.True(false, "JWT audience should be valid")
            | Jwt.ValidationResult.Malformed -> Assert.True(false, "JWT should not be malformed")
            | Jwt.ValidationResult.Valid jwtDoc ->
                let sub = jwtDoc.RootElement.GetProperty("sub").GetString()
                let tid = jwtDoc.RootElement.GetProperty("tid").GetString()
                let tn = jwtDoc.RootElement.GetProperty("tn").GetString()
                let mr = jwtDoc.RootElement.GetProperty("mr").GetString()
                let iss = jwtDoc.RootElement.GetProperty("iss").GetString()
                let aud = jwtDoc.RootElement.GetProperty("aud").GetString()
                let iat = jwtDoc.RootElement.GetProperty("iat").GetInt64()
                let exp = jwtDoc.RootElement.GetProperty("exp").GetInt64()
                test <@ sub = userId @>
                test <@ tid = tenantId @>
                test <@ tn = "Claims Tenant" @>
                test <@ mr = "owner" @>
                test <@ iss = "steward" @>
                test <@ aud = "steward-api" @>
                test <@ iat > 0L @>
                test <@ exp > iat @>
        }

    [<Fact>]
    member _.``GET /me without token returns 401``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory
            let ctx = createHttpContext factory

            do! AuthHelpers.requireAuth Auth.meHandler ctx

            test <@ ctx.Response.StatusCode = 401 @>
        }

    [<Fact>]
    member _.``GET /me with valid token returns user profile``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"me@example.com","password":"password","displayName":"Me User","tenantDisplayName":"Me Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let userId = Guid.Parse(regDoc.RootElement.GetProperty("userId").GetString())
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            let meCtx = createHttpContextWithAuth factory token
            do! Auth.meHandler meCtx

            Assert.Equal(200, meCtx.Response.StatusCode)
            let doc = readResponseJson meCtx
            Assert.Equal(userId.ToString(), doc.RootElement.GetProperty("userId").GetString())
            Assert.Equal(tenantId.ToString(), doc.RootElement.GetProperty("tenantId").GetString())
            Assert.Equal("owner", doc.RootElement.GetProperty("role").GetString())
            Assert.Equal("me@example.com", doc.RootElement.GetProperty("email").GetString())
            Assert.Equal("Me User", doc.RootElement.GetProperty("displayName").GetString())
        }

    [<Fact>]
    member _.``TenantContextMiddleware sets steward.tenant_id from JWT tid``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"rls@example.com","password":"password","displayName":"RLS User","tenantDisplayName":"RLS Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let tenantId = Guid.Parse(regDoc.RootElement.GetProperty("tenantId").GetString())
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            let httpCtx = createHttpContextWithAuth factory token
            let middleware = TenantContextMiddleware(RequestDelegate(fun _ -> Task.CompletedTask), testAuthConfig, factory)
            do! middleware.InvokeAsync(httpCtx)

            Assert.True(httpCtx.Items.ContainsKey("TenantContext"))
            let tc = httpCtx.Items["TenantContext"] :?> TenantContext
            Assert.Equal(tenantId, tc.TenantId)

            // Open a connection via the scoped helper and assert RLS GUC is set
            use! conn = TenantScopedConnection.openAsync httpCtx
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT current_setting('steward.tenant_id')"
            let! value = cmd.ExecuteScalarAsync()
            Assert.Equal(tenantId.ToString(), string value)
        }

    [<Fact>]
    member _.``Role-gated endpoint returns 403 for non-owner``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"member@example.com","password":"password","displayName":"Member","tenantDisplayName":"Member Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            // Re-issue token with role = member so we can test 403
            let userId = regDoc.RootElement.GetProperty("userId").GetString()
            let tenantId = regDoc.RootElement.GetProperty("tenantId").GetString()
            let memberToken =
                Jwt.createToken testAuthConfig.JwtSecret testAuthConfig.Issuer testAuthConfig.Audience [
                    "sub", userId
                    "tid", tenantId
                    "tn", "Member Tenant"
                    "mr", "member"
                ] (TimeSpan.FromHours(1.0))

            let adminCtx = createHttpContextWithAuth factory memberToken
            let handler = AuthHelpers.requireRole "owner" (Response.ofJson {| message = "ok" |})
            do! handler adminCtx

            Assert.Equal(403, adminCtx.Response.StatusCode)
        }

    [<Fact>]
    member _.``Role-gated endpoint returns 200 for owner``() =
        task {
            if not (canConnect ()) then return () else

            let cs = connectionString ()
            runMigrations cs
            use dataSource = NpgsqlDataSource.Create(cs)
            let factory = DbConnectionFactory(dataSource) :> IDbConnectionFactory

            let regCtx = createHttpContext factory
            setJsonBody regCtx """{"email":"owner2@example.com","password":"password","displayName":"Owner2","tenantDisplayName":"Owner2 Tenant"}"""
            do! Auth.registerHandler regCtx
            let regDoc = readResponseJson regCtx
            let token = regDoc.RootElement.GetProperty("accessToken").GetString()

            let adminCtx = createHttpContextWithAuth factory token
            let handler = AuthHelpers.requireRole "owner" (Response.ofJson {| message = "ok" |})
            do! handler adminCtx

            Assert.Equal(200, adminCtx.Response.StatusCode)
            let doc = readResponseJson adminCtx
            Assert.Equal("ok", doc.RootElement.GetProperty("message").GetString())
        }
