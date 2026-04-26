module BitThicket.Steward.Api.Test.Tests

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Xunit
open Swensen.Unquote
open BitThicket.Steward.Api

[<Fact>]
let ``sanity check`` () =
    test <@ 1 + 1 = 2 @>

[<Fact>]
let ``TenantContextMiddleware parses X-Tenant-Id and X-User-Id headers`` () =
    let ctx = DefaultHttpContext()
    let expectedTenantId = Guid.NewGuid()
    let expectedUserId = Guid.NewGuid()
    ctx.Request.Headers["X-Tenant-Id"] <- expectedTenantId.ToString()
    ctx.Request.Headers["X-User-Id"] <- expectedUserId.ToString()

    let mutable nextCalled = false
    let next = RequestDelegate(fun _ ->
        nextCalled <- true
        Task.CompletedTask)

    let middleware = TenantContextMiddleware(next)
    middleware.InvokeAsync(ctx).Wait()

    test <@ nextCalled = true @>
    test <@ ctx.Items.ContainsKey("TenantContext") @>
    let actual = ctx.Items["TenantContext"] :?> TenantContext
    test <@ actual.TenantId = expectedTenantId @>
    test <@ actual.UserId = expectedUserId @>

[<Fact>]
let ``TenantContextMiddleware ignores missing headers`` () =
    let ctx = DefaultHttpContext()

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next)
    middleware.InvokeAsync(ctx).Wait()

    test <@ not (ctx.Items.ContainsKey("TenantContext")) @>

[<Fact>]
let ``TenantContextMiddleware ignores malformed headers`` () =
    let ctx = DefaultHttpContext()
    ctx.Request.Headers["X-Tenant-Id"] <- "not-a-guid"
    ctx.Request.Headers["X-User-Id"] <- "also-not-a-guid"

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next)
    middleware.InvokeAsync(ctx).Wait()

    test <@ not (ctx.Items.ContainsKey("TenantContext")) @>

[<Fact>]
let ``TenantContextMiddleware ignores partial headers`` () =
    let ctx = DefaultHttpContext()
    ctx.Request.Headers["X-Tenant-Id"] <- Guid.NewGuid().ToString()

    let next = RequestDelegate(fun _ -> Task.CompletedTask)
    let middleware = TenantContextMiddleware(next)
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
