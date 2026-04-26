namespace BitThicket.Steward.Api

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

/// Identifies the tenant and user for the current request/operation.
/// The persistence layer uses this to set `steward.tenant_id` and
/// `steward.user_id` on every Npgsql connection so that downstream RLS
/// policies can enforce isolation.
type TenantContext = {
    TenantId: Guid
    UserId: Guid
}

/// Provides access to the current request's tenant context.
type ITenantContextAccessor =
    abstract Context : TenantContext option

/// Reads TenantContext from the current HttpContext.Items (populated by
/// TenantContextMiddleware via the X-Tenant-Id and X-User-Id headers).
type TenantContextAccessor(httpContextAccessor: IHttpContextAccessor) =
    interface ITenantContextAccessor with
        member _.Context =
            match httpContextAccessor.HttpContext with
            | null -> None
            | ctx ->
                match ctx.Items.TryGetValue("TenantContext") with
                | true, (:? TenantContext as tc) -> Some tc
                | _ -> None

/// ASP.NET Core middleware that extracts the X-Tenant-Id and X-User-Id
/// request headers and stores a TenantContext value in HttpContext.Items
/// for the scoped ITenantContextAccessor to consume.
///
/// NOTE: Header-based extraction is a stop-gap until JWT auth (STE-18)
/// is in place. At that point the middleware will derive UserId from
/// the authenticated claims instead of a raw header.
type TenantContextMiddleware(next: RequestDelegate) =
    member _.InvokeAsync(ctx: HttpContext) =
        task {
            let tryParseGuid (values: Microsoft.Extensions.Primitives.StringValues) =
                if values.Count > 0 then
                    match Guid.TryParse(values.[0]) with
                    | true, g -> Some g
                    | _ -> None
                else
                    None

            let tenantId =
                match ctx.Request.Headers.TryGetValue("X-Tenant-Id") with
                | true, v -> tryParseGuid v
                | _ -> None

            let userId =
                match ctx.Request.Headers.TryGetValue("X-User-Id") with
                | true, v -> tryParseGuid v
                | _ -> None

            match tenantId, userId with
            | Some tid, Some uid ->
                ctx.Items["TenantContext"] <- { TenantId = tid; UserId = uid }
            | _ ->
                // Either header missing or malformed — ignored. Auth middleware
                // (STE-17/18) will reject requests without a valid tenant context.
                ()

            return! next.Invoke(ctx)
        }

module TenantContextServices =
    /// Register tenant-context services in the DI container.
    let register (services: IServiceCollection) =
        services.AddHttpContextAccessor() |> ignore
        services.AddScoped<ITenantContextAccessor, TenantContextAccessor>() |> ignore
        services
