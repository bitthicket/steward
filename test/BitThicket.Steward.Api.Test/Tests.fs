module BitThicket.Steward.Api.Test.Tests

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open Falco
open BitThicket.Steward.Api

let private testAuthConfig = {
    JwtSecret = "test-secret-key-for-unit-tests-only-do-not-use-in-production"
    JwtSecretPrevious = None
    Issuer = "steward"
    Audience = "steward-api"
}

let private dummyDbFactory =
    { new IDbConnectionFactory with
        member _.OpenAsync() = failwith "not used"
        member _.OpenForTenantAsync(_) = failwith "not used" }

let private makeToken (claims: (string * string) list) =
    Jwt.createToken testAuthConfig.JwtSecret testAuthConfig.Issuer testAuthConfig.Audience claims (TimeSpan.FromHours(1.0))

[<Fact>]
let ``sanity check`` () =
    test <@ 1 + 1 = 2 @>

[<Fact>]
let ``TenantContextMiddleware parses valid JWT Bearer token`` () =
    let expectedTenantId = Guid.NewGuid()
    let expectedUserId = Guid.NewGuid()
    let token = makeToken [
        "sub", expectedUserId.ToString()
        "tid", expectedTenantId.ToString()
        "mr", "owner"
    ]

    let ctx = DefaultHttpContext()
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"

    let mutable nextCalled = false
    let next = RequestDelegate(fun _ ->
        nextCalled <- true
        Task.CompletedTask)

    let middleware = TenantContextMiddleware(next, testAuthConfig, dummyDbFactory)
    middleware.InvokeAsync(ctx).Wait()

    test <@ nextCalled = true @>
    test <@ ctx.Items.ContainsKey("TenantContext") @>
    let actual = ctx.Items["TenantContext"] :?> TenantContext
    test <@ actual.TenantId = expectedTenantId @>
    test <@ actual.UserId = expectedUserId @>
    test <@ ctx.Items["TenantRole"] :?> string = "owner" @>

[<Fact>]
let ``TenantContextMiddleware ignores missing Authorization header`` () =
    let ctx = DefaultHttpContext()

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next, testAuthConfig, dummyDbFactory)
    middleware.InvokeAsync(ctx).Wait()

    test <@ not (ctx.Items.ContainsKey("TenantContext")) @>

[<Fact>]
let ``TenantContextMiddleware ignores malformed Authorization header`` () =
    let ctx = DefaultHttpContext()
    ctx.Request.Headers["Authorization"] <- "Basic dXNlcjpwYXNz"

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next, testAuthConfig, dummyDbFactory)
    middleware.InvokeAsync(ctx).Wait()

    test <@ not (ctx.Items.ContainsKey("TenantContext")) @>

[<Fact>]
let ``TenantContextMiddleware ignores invalid JWT signature`` () =
    let token = Jwt.createToken "wrong-secret" testAuthConfig.Issuer testAuthConfig.Audience ["sub","a";"tid","b"] (TimeSpan.FromHours(1.0))
    let ctx = DefaultHttpContext()
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next, testAuthConfig, dummyDbFactory)
    middleware.InvokeAsync(ctx).Wait()

    test <@ not (ctx.Items.ContainsKey("TenantContext")) @>

[<Fact>]
let ``TenantContextMiddleware accepts token signed with previous secret`` () =
    let expectedTenantId = Guid.NewGuid()
    let expectedUserId = Guid.NewGuid()
    let configWithPrevious = { testAuthConfig with JwtSecret = "new-secret"; JwtSecretPrevious = Some testAuthConfig.JwtSecret }
    let token = makeToken [
        "sub", expectedUserId.ToString()
        "tid", expectedTenantId.ToString()
        "mr", "member"
    ]

    let ctx = DefaultHttpContext()
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next, configWithPrevious, dummyDbFactory)
    middleware.InvokeAsync(ctx).Wait()

    test <@ ctx.Items.ContainsKey("TenantContext") @>
    let actual = ctx.Items["TenantContext"] :?> TenantContext
    test <@ actual.TenantId = expectedTenantId @>
    test <@ ctx.Items["TenantRole"] :?> string = "member" @>

[<Fact>]
let ``TenantContextMiddleware ignores expired JWT`` () =
    let token = Jwt.createToken testAuthConfig.JwtSecret testAuthConfig.Issuer testAuthConfig.Audience ["sub","a";"tid","b"] (TimeSpan.FromSeconds(-1.0))
    let ctx = DefaultHttpContext()
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next, testAuthConfig, dummyDbFactory)
    middleware.InvokeAsync(ctx).Wait()

    test <@ not (ctx.Items.ContainsKey("TenantContext")) @>

[<Fact>]
let ``TenantContextMiddleware ignores wrong issuer`` () =
    let token = Jwt.createToken testAuthConfig.JwtSecret "wrong-issuer" testAuthConfig.Audience ["sub","a";"tid","b"] (TimeSpan.FromHours(1.0))
    let ctx = DefaultHttpContext()
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next, testAuthConfig, dummyDbFactory)
    middleware.InvokeAsync(ctx).Wait()

    test <@ not (ctx.Items.ContainsKey("TenantContext")) @>

[<Fact>]
let ``TenantContextMiddleware ignores wrong audience`` () =
    let token = Jwt.createToken testAuthConfig.JwtSecret testAuthConfig.Issuer "wrong-audience" ["sub","a";"tid","b"] (TimeSpan.FromHours(1.0))
    let ctx = DefaultHttpContext()
    ctx.Request.Headers["Authorization"] <- $"Bearer {token}"

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next, testAuthConfig, dummyDbFactory)
    middleware.InvokeAsync(ctx).Wait()

    test <@ not (ctx.Items.ContainsKey("TenantContext")) @>

[<Fact>]
let ``TenantContextAccessor returns None when no context is set`` () =
    let httpContextAccessor =
        { new IHttpContextAccessor with
            member _.HttpContext = DefaultHttpContext()
            member _.HttpContext with set _ = () }

    let accessor = TenantContextAccessor(httpContextAccessor) :> ITenantContextAccessor
    test <@ accessor.Context = None @>

[<Fact>]
let ``TenantContextAccessor returns Some when context is set`` () =
    let expectedTenantId = Guid.NewGuid()
    let expectedUserId = Guid.NewGuid()
    let httpContext = DefaultHttpContext()
    httpContext.Items["TenantContext"] <- { TenantId = expectedTenantId; UserId = expectedUserId }

    let httpContextAccessor =
        { new IHttpContextAccessor with
            member _.HttpContext = httpContext
            member _.HttpContext with set _ = () }

    let accessor = TenantContextAccessor(httpContextAccessor) :> ITenantContextAccessor
    test <@ accessor.Context = Some { TenantId = expectedTenantId; UserId = expectedUserId } @>

[<Fact>]
let ``requireAuth returns 401 when no tenant context`` () =
    let ctx = DefaultHttpContext()
    ctx.Response.Body <- new System.IO.MemoryStream()

    let services = ServiceCollection()
    services.AddSingleton<ITenantContextAccessor>(TenantContextAccessor({ new IHttpContextAccessor with
        member _.HttpContext = ctx
        member _.HttpContext with set _ = () })) |> ignore
    let provider = services.BuildServiceProvider()
    ctx.RequestServices <- provider

    let handler = AuthHelpers.requireAuth (Response.ofPlainText "should not reach")
    handler ctx |> Async.AwaitTask |> Async.RunSynchronously

    test <@ ctx.Response.StatusCode = 401 @>

[<Fact>]
let ``requireAuth passes through when tenant context is present`` () =
    let ctx = DefaultHttpContext()
    ctx.Response.Body <- new System.IO.MemoryStream()
    ctx.Items["TenantContext"] <- { TenantId = Guid.NewGuid(); UserId = Guid.NewGuid() }

    let services = ServiceCollection()
    services.AddSingleton<ITenantContextAccessor>(TenantContextAccessor({ new IHttpContextAccessor with
        member _.HttpContext = ctx
        member _.HttpContext with set _ = () })) |> ignore
    let provider = services.BuildServiceProvider()
    ctx.RequestServices <- provider

    let mutable called = false
    let handler = AuthHelpers.requireAuth (fun _ -> called <- true; Task.CompletedTask)
    handler ctx |> Async.AwaitTask |> Async.RunSynchronously

    test <@ called = true @>

[<Fact>]
let ``requireRole returns 403 when role does not match`` () =
    let ctx = DefaultHttpContext()
    ctx.Response.Body <- new System.IO.MemoryStream()
    ctx.Items["TenantContext"] <- { TenantId = Guid.NewGuid(); UserId = Guid.NewGuid() }
    ctx.Items["TenantRole"] <- "member"

    let services = ServiceCollection()
    services.AddSingleton<ITenantContextAccessor>(TenantContextAccessor({ new IHttpContextAccessor with
        member _.HttpContext = ctx
        member _.HttpContext with set _ = () })) |> ignore
    let provider = services.BuildServiceProvider()
    ctx.RequestServices <- provider

    let handler = AuthHelpers.requireRole "owner" (Response.ofPlainText "should not reach")
    handler ctx |> Async.AwaitTask |> Async.RunSynchronously

    test <@ ctx.Response.StatusCode = 403 @>

[<Fact>]
let ``requireRole passes through when role matches`` () =
    let ctx = DefaultHttpContext()
    ctx.Response.Body <- new System.IO.MemoryStream()
    ctx.Items["TenantContext"] <- { TenantId = Guid.NewGuid(); UserId = Guid.NewGuid() }
    ctx.Items["TenantRole"] <- "owner"

    let services = ServiceCollection()
    services.AddSingleton<ITenantContextAccessor>(TenantContextAccessor({ new IHttpContextAccessor with
        member _.HttpContext = ctx
        member _.HttpContext with set _ = () })) |> ignore
    let provider = services.BuildServiceProvider()
    ctx.RequestServices <- provider

    let mutable called = false
    let handler = AuthHelpers.requireRole "owner" (fun _ -> called <- true; Task.CompletedTask)
    handler ctx |> Async.AwaitTask |> Async.RunSynchronously

    test <@ called = true @>
